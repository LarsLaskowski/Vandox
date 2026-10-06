package importer

// format is the container format of a file's content.
type format int

const (
	formatPlain format = iota
	formatEmpty
	formatGzip
	formatTar
	formatUnsupported
)

// sniffFormat classifies head; for formatUnsupported it also returns the format's name.
func sniffFormat(head []byte) (format, string) {
	return formatPlain, ""
}
