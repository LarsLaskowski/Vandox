namespace Vandox.Core.LogParsing;

/// <summary>
/// One row of the process table of top.
/// </summary>
/// <param name="Pid">The process id</param>
/// <param name="User">The user</param>
/// <param name="State">The state letter</param>
/// <param name="CpuPercent">The share of one CPU in percent</param>
/// <param name="ResBytes">The resident memory in bytes</param>
/// <param name="Command">The COMMAND column</param>
internal sealed record LegacyTopRow(int Pid, string User, string State, double CpuPercent, ulong ResBytes, string Command);