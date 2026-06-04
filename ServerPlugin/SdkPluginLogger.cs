using System;
using System.Runtime.CompilerServices;
using PluginSdk.Logging;
using Shared.Logging;

namespace ServerPlugin;

public class SdkPluginLogger : LogFormatter, IPluginLogger
{
    private readonly Logger _logger;

    public SdkPluginLogger(string pluginName) : base("")
    {
        _logger = Logger.Create(pluginName);
    }

    public bool IsTraceEnabled => true;
    public bool IsDebugEnabled => true;
    public bool IsInfoEnabled => true;
    public bool IsWarningEnabled => true;
    public bool IsErrorEnabled => true;
    public bool IsCriticalEnabled => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Debug, "Trace", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Debug, "Debug", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Info, "Info", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Warning, "Warning", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Error, "Error", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(Exception ex, string message, params object[] data)
    {
        WriteLog(LogLevel.Critical, "Critical", ex, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string message, params object[] data)
    {
        Trace(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Debug(string message, params object[] data)
    {
        Debug(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Info(string message, params object[] data)
    {
        Info(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Warning(string message, params object[] data)
    {
        Warning(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Error(string message, params object[] data)
    {
        Error(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Critical(string message, params object[] data)
    {
        Critical(null, message, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteLog(LogLevel logLevel, string forwardedLevel, Exception ex, string message, params object[] data)
    {
        var formatted = Format(ex, message, data);
        _logger.Log(logLevel, formatted, ex);
        Plugin.Instance?.SocketClient?.QueueLog(forwardedLevel, formatted);
    }
}
