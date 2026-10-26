using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Replays an agent as it collects and sends: its records on the collector grid, its numbers, the live stream every 4 seconds and
/// the backfill beside it, and the <c>agent_send_lag_seconds</c> of every <c>self</c> sample derived as the sender does, never chosen by a test.
/// The model: time is the backend host's clock in whole seconds, the agent stamps records with that time plus its skew. Every 30 seconds
/// from its start the agent captures a <c>system</c> metric and, on every second tick, a <c>self</c> sample (the first tick of a start
/// included); optionally a log line every n seconds. Every 4 seconds, from one second after the start, both streams send when the
/// backend is up and every request is acknowledged at once. The live stream sends the records numbered above the newest one when the
/// backend came up, the backfill stream sends the older unacknowledged ones oldest first, a live batch before a backfill batch; the
/// backfill waits until the live stream has sent its first batch after the backend came up (the current batch arrives first). The
/// sample's send lag is its time minus the capture time of the newest acknowledged record since the start, before the first
/// acknowledgement minus the start, never negative, on the agent's clock.
/// </summary>
internal sealed class AgentReplay
{
    #region Constants

    private const int SendPeriodSeconds = 4;

    private const int SystemPeriodSeconds = 30;

    private const int MaxLiveBatch = 1000;

    #endregion // Constants

    #region Fields

    private readonly SortedDictionary<ulong, DataRecord> _spool = [];

    private readonly Dictionary<ulong, DateTimeOffset> _trueCaptures = [];

    private readonly DateTimeOffset _sendAnchor;

    private readonly int _backfillBatchSize;

    private readonly int _logEverySeconds;

    private readonly TimeSpan _delay;

    private DateTimeOffset _nextTick;

    private DateTimeOffset _startStamp;

    private DateTimeOffset? _newestAckedStamp;

    private int _tickIndex;

    private ulong _nextSeq;

    private ulong _liveCursor;

    private ulong _backfillLimit;

    private bool _backendUp;

    private bool _backfillHeld;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentReplay"/> class.
    /// </summary>
    /// <param name="start">The backend host's time the agent starts at, in whole seconds</param>
    /// <param name="skew">The agent's clock minus the backend host's clock</param>
    /// <param name="firstSeq">The first sequence number</param>
    /// <param name="backendUp"><see langword="true"/> when the backend is up from the start</param>
    /// <param name="backfillBatchSize">The most records of a backfill batch</param>
    /// <param name="logEverySeconds">The seconds between two log lines, 0 for an agent without log traffic</param>
    /// <param name="delay">The time a request takes to arrive</param>
    internal AgentReplay(DateTimeOffset start, TimeSpan skew, ulong firstSeq, bool backendUp = true, int backfillBatchSize = 500, int logEverySeconds = 0, TimeSpan delay = default)
    {
        Skew = skew;
        _backfillBatchSize = backfillBatchSize;
        _logEverySeconds = logEverySeconds;
        _delay = delay;
        Now = start.AddSeconds(-1);
        _nextTick = start;
        _startStamp = start + skew;
        _sendAnchor = start.AddSeconds(1);
        _nextSeq = firstSeq;
        _liveCursor = firstSeq - 1;
        _backfillLimit = firstSeq - 1;
        _backendUp = backendUp;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the agent ID of the batches.
    /// </summary>
    internal string AgentId { get; } = TimelineSamples.AgentId;

    /// <summary>
    /// Gets the boot ID of the batches.
    /// </summary>
    internal string BootId { get; } = TimelineSamples.BootId;

    /// <summary>
    /// Gets the backend host's time the replay has reached.
    /// </summary>
    internal DateTimeOffset Now { get; private set; }

    /// <summary>
    /// Gets the agent's clock minus the backend host's clock.
    /// </summary>
    internal TimeSpan Skew { get; private set; }

    /// <summary>
    /// Gets the number the agent assigned last.
    /// </summary>
    internal ulong LastSeq => _nextSeq - 1;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Brings the backend up or takes it down.
    /// </summary>
    /// <param name="up"><see langword="true"/> to bring it up</param>
    internal void SetBackend(bool up)
    {
        if (up && _backendUp == default)
        {
            _liveCursor = _nextSeq - 1;
            _backfillLimit = _nextSeq - 1;
            _backfillHeld = _spool.Keys.Any(seq => seq <= _backfillLimit);
        }

        _backendUp = up;
    }

    /// <summary>
    /// Restarts the agent cleanly in the same boot: the numbers and the spool continue, the send lag counts from this start again.
    /// </summary>
    internal void Restart()
    {
        _startStamp = Now + Skew;
        _nextTick = Now.AddSeconds(1);
        _tickIndex = 0;
        _newestAckedStamp = null;
    }

    /// <summary>
    /// Steps the agent's clock.
    /// </summary>
    /// <param name="step">The step; negative steps it back</param>
    internal void StepClock(TimeSpan step)
    {
        Skew += step;
    }

    /// <summary>
    /// Takes a sequence number the replay will not use, for a record a test hands-builds.
    /// </summary>
    /// <returns>The number</returns>
    internal ulong ReserveSeq()
    {
        return _nextSeq++;
    }

    /// <summary>
    /// Gets the backend host's time a record was captured at.
    /// </summary>
    /// <param name="seq">The number of the record</param>
    /// <returns>The capture time without the skew</returns>
    internal DateTimeOffset TrueCaptureOf(ulong seq)
    {
        return _trueCaptures[seq];
    }

    /// <summary>
    /// Runs the replay on and collects the batches it sends.
    /// </summary>
    /// <param name="until">The backend host's time to run to</param>
    /// <returns>The batches in the order they arrive</returns>
    internal List<ReplayBatch> AdvanceTo(DateTimeOffset until)
    {
        var batches = new List<ReplayBatch>();

        while (Now < until)
        {
            Now = Now.AddSeconds(1);

            Collect();

            if (_backendUp && (long)(Now - _sendAnchor).TotalSeconds % SendPeriodSeconds == 0)
            {
                Send(batches);
            }
        }

        return batches;
    }

    /// <summary>
    /// Builds a metric record.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="value">The value</param>
    /// <param name="unit">The unit</param>
    /// <param name="stamp">The capture time</param>
    /// <returns>The record</returns>
    private static DataRecord Metric(string source, string name, double value, string unit, DateTimeOffset stamp)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = source,
                   CapturedAt = stamp,
                   Data = new MetricPoint
                          {
                              Name = name,
                              Value = value,
                              Unit = unit
                          }
               };
    }

    /// <summary>
    /// Captures the records that are due at the current time.
    /// </summary>
    private void Collect()
    {
        if (Now >= _nextTick)
        {
            var stamp = Now + Skew;

            Add(Metric("system", "cpu_user_percent", _tickIndex % 100, "percent", stamp));

            if (_tickIndex % 2 == 0)
            {
                Add(Metric("agent", "agent_send_lag_seconds", SendLagSeconds(stamp), "seconds", stamp));
            }

            _tickIndex++;
            _nextTick = _nextTick.AddSeconds(SystemPeriodSeconds);
        }

        if (_logEverySeconds > 0 && (long)(Now - _sendAnchor).TotalSeconds % _logEverySeconds == 0)
        {
            Add(new DataRecord
                {
                    Origin = RecordOrigin.Agent,
                    Source = "syslog",
                    CapturedAt = Now + Skew,
                    Data = new LogLine
                           {
                               Log = "syslog",
                               Host = "web-1",
                               Program = "sshd",
                               Message = "session opened"
                           }
                });
        }
    }

    /// <summary>
    /// Gets the send lag of a sample as the sender measures it.
    /// </summary>
    /// <param name="stamp">The sample's time on the agent's clock</param>
    /// <returns>The lag in seconds</returns>
    private double SendLagSeconds(DateTimeOffset stamp)
    {
        var reference = _newestAckedStamp ?? _startStamp;

        return Math.Max(0, (stamp - reference).TotalSeconds);
    }

    /// <summary>
    /// Numbers a record and puts it into the spool.
    /// </summary>
    /// <param name="record">The record</param>
    private void Add(DataRecord record)
    {
        record.Seq = _nextSeq;
        _spool.Add(_nextSeq, record);
        _trueCaptures.Add(_nextSeq, Now);
        _nextSeq++;
    }

    /// <summary>
    /// Sends the live stream and then the backfill stream, and acknowledges what they send.
    /// </summary>
    /// <param name="batches">The batches sent so far</param>
    private void Send(List<ReplayBatch> batches)
    {
        var live = _spool.Where(pair => pair.Key > _liveCursor).Take(MaxLiveBatch).ToList();

        if (live.Count > 0)
        {
            _liveCursor = live[^1].Key;
            _backfillHeld = false;
            batches.Add(new ReplayBatch(Package(live), false, true));
            Acknowledge(live);
        }

        if (_backfillHeld)
        {
            return;
        }

        var backfill = _spool.Where(pair => pair.Key <= _backfillLimit).Take(_backfillBatchSize).ToList();

        if (backfill.Count > 0)
        {
            batches.Add(new ReplayBatch(Package(backfill), true, false));
            Acknowledge(backfill);
        }
    }

    /// <summary>
    /// Builds the batch of spooled records.
    /// </summary>
    /// <param name="records">The records, oldest first</param>
    /// <returns>The batch</returns>
    private RecordBatch Package(List<KeyValuePair<ulong, DataRecord>> records)
    {
        return new RecordBatch
               {
                   AgentId = AgentId,
                   BootId = BootId,
                   ReceivedAt = Now + _delay,
                   Records = records.Select(pair => pair.Value).ToArray()
               };
    }

    /// <summary>
    /// Takes acknowledged records out of the spool.
    /// </summary>
    /// <param name="records">The records</param>
    private void Acknowledge(List<KeyValuePair<ulong, DataRecord>> records)
    {
        foreach (var (seq, record) in records)
        {
            _spool.Remove(seq);

            if (_newestAckedStamp is null || record.CapturedAt > _newestAckedStamp)
            {
                _newestAckedStamp = record.CapturedAt;
            }
        }
    }

    #endregion // Methods
}