package mariadb

import (
	"io/fs"
	"math"
	"math/bits"
	"net/netip"
	"slices"
	"strconv"

	"github.com/LarsLaskowski/Vandox/internal/connstats"
	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Names and units of the metrics of the collector.
const (
	metricMemoryMaxBytes   = "mariadb_memory_max_bytes"
	metricMemoryMaxPercent = "mariadb_memory_max_ram_percent"
	metricQuestionsRate    = "mariadb_questions_per_second"
	metricPoolUsedPercent  = "mariadb_innodb_buffer_pool_used_percent"
	metricPoolHitPercent   = "mariadb_innodb_buffer_pool_read_hit_percent"
	metricExposedListeners = "mariadb_exposed_listeners"
	unitBytes              = "bytes"
	unitPercent            = "percent"
	unitPerSecond          = "per_second"
)

// u128 is an unsigned 128-bit number.
type u128 struct{ hi, lo uint64 }

// add returns v + n; ok is false when the sum does not fit.
func (v u128) add(n u128) (sum u128, ok bool) {
	lo, carry := bits.Add64(v.lo, n.lo, 0)
	hi, carry := bits.Add64(v.hi, n.hi, carry)
	return u128{hi, lo}, carry == 0
}

// mul returns v × n; ok is false when the product does not fit.
func (v u128) mul(n uint64) (product u128, ok bool) {
	h0, lo := bits.Mul64(v.lo, n)
	h1, l1 := bits.Mul64(v.hi, n)
	hi, carry := bits.Add64(h0, l1, 0)
	return u128{hi, lo}, h1 == 0 && carry == 0
}

// float returns the number as a float.
func (v u128) float() float64 { return math.Ldexp(float64(v.hi), 64) + float64(v.lo) }

// String returns the decimal digits of the number.
func (v u128) String() string {
	const base = 10_000_000_000_000_000_000
	var chunks [3]uint64
	n := 0
	for v.hi != 0 || v.lo != 0 {
		q1, r1 := bits.Div64(0, v.hi, base)
		q0, r0 := bits.Div64(r1, v.lo, base)
		v, chunks[n] = u128{q1, q0}, r0
		n++
	}
	if n == 0 {
		return "0"
	}
	out := strconv.FormatUint(chunks[n-1], 10)
	for i := n - 2; i >= 0; i-- {
		digits := strconv.FormatUint(chunks[i], 10)
		out += "0000000000000000000"[len(digits):] + digits
	}
	return out
}

// variableNumber reads a variable that counts as 0 when missing; ok is false when it is present and no plain number.
func variableNumber(vars map[string]string, name string) (n uint64, ok bool) {
	text, present := vars[name]
	if !present {
		return 0, true
	}
	return parseDigits([]byte(text))
}

// sumVariables adds the variables; ok is false when one is no plain number or the sum does not fit.
func sumVariables(vars map[string]string, names ...string) (sum u128, ok bool) {
	for _, name := range names {
		n, valid := variableNumber(vars, name)
		if !valid {
			return u128{}, false
		}
		if sum, ok = sum.add(u128{lo: n}); !ok {
			return u128{}, false
		}
	}
	return sum, true
}

// memoryMax returns the memory the server may use as hi and lo parts: the global buffers and max_connections times the
// buffers of a connection. ok is false when a required value is missing or a value that counts is no plain number.
func memoryMax(vars map[string]string) (hi, lo uint64, ok bool) {
	_, hasPool := vars["innodb_buffer_pool_size"]
	_, hasConnections := vars["max_connections"]
	connections, validConnections := variableNumber(vars, "max_connections")
	tmp, validTmp := variableNumber(vars, "tmp_table_size")
	heapSize, validHeap := variableNumber(vars, "max_heap_table_size")
	global, validGlobal := sumVariables(vars, "innodb_buffer_pool_size", "innodb_log_buffer_size", "key_buffer_size", "aria_pagecache_buffer_size", "query_cache_size")
	perConnection, validPer := sumVariables(vars, "sort_buffer_size", "read_buffer_size", "read_rnd_buffer_size", "join_buffer_size", "thread_stack")
	if !hasPool || !hasConnections || !validConnections || !validTmp || !validHeap || !validGlobal || !validPer {
		return 0, 0, false
	}
	if vars["log_bin"] == "ON" {
		binlog, ok := sumVariables(vars, "binlog_cache_size")
		if !ok {
			return 0, 0, false
		}
		if perConnection, ok = perConnection.add(binlog); !ok {
			return 0, 0, false
		}
	}
	total, fits := global.add(u128{lo: min(tmp, heapSize)})
	if !fits {
		return 0, 0, false
	}
	product, fits := perConnection.mul(connections)
	if !fits {
		return 0, 0, false
	}
	if total, fits = total.add(product); !fits {
		return 0, 0, false
	}
	return total.hi, total.lo, true
}

// increase returns the increase of a counter between two samples; ok is false when it is missing in one or fell.
func increase(prev, cur *sample, name string) (uint64, bool) {
	before, okBefore := prev.status[name]
	after, okAfter := cur.status[name]
	if !okBefore || !okAfter || after < before {
		return 0, false
	}
	return after - before, true
}

// rates returns the rate metrics between two samples.
func rates(prev, cur *sample) []model.MetricPoint {
	if prev == nil || cur == nil {
		return nil
	}
	seconds := cur.run.Sub(prev.run).Seconds()
	if seconds <= 0 {
		return nil
	}
	var out []model.MetricPoint
	if questions, ok := increase(prev, cur, "Questions"); ok {
		out = append(out, model.MetricPoint{Name: metricQuestionsRate, Value: float64(questions) / seconds, Unit: unitPerSecond})
	}
	reads, okReads := increase(prev, cur, "Innodb_buffer_pool_reads")
	requests, okRequests := increase(prev, cur, "Innodb_buffer_pool_read_requests")
	if okReads && okRequests && requests > 0 && reads <= requests {
		out = append(out, model.MetricPoint{Name: metricPoolHitPercent, Value: 100 * (1 - float64(reads)/float64(requests)), Unit: unitPercent})
	}
	return out
}

// bufferPoolUsed returns the used share of the buffer pool in percent.
func bufferPoolUsed(status map[string]uint64) (float64, bool) {
	total, okTotal := status["Innodb_buffer_pool_pages_total"]
	free, okFree := status["Innodb_buffer_pool_pages_free"]
	if !okTotal || !okFree || total == 0 || free > total {
		return 0, false
	}
	return 100 * float64(total-free) / float64(total), true
}

// exposed reports whether addr is reachable from outside the host: every address that is not loopback.
func exposed(addr netip.Addr) bool {
	return !addr.Unmap().IsLoopback()
}

// countExposed counts the listening TCP sockets on the ports in ports that are exposed.
func countExposed(proc fs.FS, ports []uint16) (int, error) {
	n := 0
	err := connstats.TCPListeners(proc, func(local netip.AddrPort) {
		if slices.Contains(ports, local.Port()) && exposed(local.Addr()) {
			n++
		}
	})
	if err != nil {
		return 0, err
	}
	return n, nil
}

// listenPorts returns the ports MariaDB listens on from its variables: port, and extra_port when it is not 0. ok is false when
// port is missing or not a number from 1 to 65535 or extra_port is present and not a number from 0 to 65535.
func listenPorts(vars map[string]string) (ports []uint16, ok bool) {
	port, valid := portOf(vars, "port")
	if !valid || port == 0 {
		return nil, false
	}
	ports = []uint16{port}
	if _, present := vars["extra_port"]; !present {
		return ports, true
	}
	extra, valid := portOf(vars, "extra_port")
	if !valid {
		return nil, false
	}
	if extra != 0 {
		ports = append(ports, extra)
	}
	return ports, true
}

// portOf reads a variable as a port number from 0 to 65535.
func portOf(vars map[string]string, name string) (uint16, bool) {
	n, ok := parseDigits([]byte(vars[name]))
	if !ok || n > math.MaxUint16 {
		return 0, false
	}
	return uint16(n), true
}
