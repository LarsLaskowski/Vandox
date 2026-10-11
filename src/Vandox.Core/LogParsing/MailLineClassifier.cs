using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Classifies the lines of Postfix and Dovecot that the journal and syslog parsers read: the severity marker sets the
/// priority, fatal and panic lines and failed or lost MySQL connections get an event.
/// </summary>
internal static class MailLineClassifier
{
    #region Methods

    /// <summary>
    /// Determines whether a program is a Postfix daemon.
    /// </summary>
    /// <param name="program">The program as the syslog tag or journal identifier gives it</param>
    /// <returns><c>true</c> for <c>postfix/&lt;daemon&gt;</c> and <c>postfix-&lt;name&gt;/&lt;daemon&gt;</c></returns>
    internal static bool IsPostfix(string program)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Determines whether a program is Dovecot.
    /// </summary>
    /// <param name="program">The program as the syslog tag or journal identifier gives it</param>
    /// <returns><c>true</c> for <c>dovecot</c> only</returns>
    internal static bool IsDovecot(string program)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Sets priority and event of a Postfix or Dovecot line from its severity marker; other lines stay unchanged.
    /// </summary>
    /// <param name="payload">The line</param>
    internal static void Classify(LogLine payload)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}