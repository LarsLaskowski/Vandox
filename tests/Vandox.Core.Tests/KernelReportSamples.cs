namespace Vandox.Core.Tests;

/// <summary>
/// Kernel report samples: real, shortened messages of a Linux 5.15 OOM report and warning report, as they follow the <c>kernel:</c> tag.
/// </summary>
internal static class KernelReportSamples
{
    #region Fields

    /// <summary>
    /// The messages of the OOM report from the line with <c>invoked oom-killer</c> to the kill line; the fixture <c>testdata/logs/kern.log-oom</c> holds them.
    /// </summary>
    internal static readonly string[] Oom = [
                                                "[123456.789012] mariadbd invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0",
                                                "[123456.789020] CPU: 1 PID: 4242 Comm: mariadbd Not tainted 5.15.0-91-generic #101-Ubuntu",
                                                "[123456.789025] Hardware name: QEMU Standard PC (i440FX + PIIX, 1996), BIOS 1.15.0-1 04/01/2014",
                                                "[123456.789030] Call Trace:",
                                                "[123456.789032]  <TASK>",
                                                "[123456.789035]  dump_stack_lvl+0x4a/0x63",
                                                "[123456.789040]  dump_header+0x53/0x224",
                                                "[123456.789045]  oom_kill_process.cold+0xb/0x10",
                                                "[123456.789050]  out_of_memory+0x1e1/0x2e0",
                                                "[123456.789055]  </TASK>",
                                                "[123456.789060] Mem-Info:",
                                                "[123456.789065] active_anon:1900000 inactive_anon:50000 isolated_anon:0",
                                                "[123456.789070] Node 0 active_anon:7600000kB inactive_anon:200000kB active_file:1200kB inactive_file:900kB unevictable:0kB",
                                                "[123456.789075] Node 0 DMA32 free:12000kB min:7000kB low:8800kB high:10600kB reserved_highatomic:0KB",
                                                "[123456.789080] Tasks state (memory values in pages):",
                                                "[123456.789085] [  pid  ]   uid  tgid total_vm      rss pgtables_bytes swapents oom_score_adj name",
                                                "[123456.789090] [    512]     0   512    33000     1200   200000        0          -250 systemd-journal",
                                                "[123456.789095] [   4242]   110  4242  2100000  1850000 15000000        0             0 mariadbd",
                                                "[123456.789100] oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/system.slice/mariadb.service,task=mariadbd,pid=4242,uid=110",
                                                "[123456.789105] Out of memory: Killed process 4242 (mariadbd) total-vm:8400000kB, anon-rss:7400000kB, file-rss:0kB, shmem-rss:0kB, UID:110 pgtables:14600kB oom_score_adj:0"
                                            ];

    /// <summary>
    /// The ordinary kernel message that precedes the report in the fixture.
    /// </summary>
    internal static readonly string Before = "[123400.111111] TCP: request_sock_TCP: Possible SYN flooding on port 80. Sending cookies.  Check SNMP counters.";

    /// <summary>
    /// The ordinary kernel message that follows the report in the fixture.
    /// </summary>
    internal static readonly string After = "[123460.222222] oom_reaper: reaped process 4242 (mariadbd), now anon-rss:0kB, file-rss:0kB, shmem-rss:0kB";

    /// <summary>
    /// The messages of a warning report from <c>cut here</c> to <c>end trace</c>.
    /// </summary>
    internal static readonly string[] CutHere = [
                                                    "[  123.456789] ------------[ cut here ]------------",
                                                    "[  123.456790] WARNING: CPU: 1 PID: 99 at drivers/net/foo.c:12 foo_xmit+0x12/0x30 [foo]",
                                                    "[  123.456791] Modules linked in: foo xt_conntrack nf_conntrack",
                                                    "[  123.456792] CPU: 1 PID: 99 Comm: bash Not tainted 5.15.0-91-generic #101-Ubuntu",
                                                    "[  123.456793] Call Trace:",
                                                    "[  123.456794]  <TASK>",
                                                    "[  123.456795]  dev_hard_start_xmit+0xd0/0x230",
                                                    "[  123.456796]  </TASK>",
                                                    "[  123.456797] ---[ end trace 0000000000000000 ]---"
                                                ];

    #endregion // Fields
}