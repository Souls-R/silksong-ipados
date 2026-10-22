// Plugin logging, routed into Unity's log.
//
// On a PC BepInEx owns a console window and a log file. Here the only channel
// that reaches a developer is logcat, which is where UnityEngine.Debug already
// goes -- `make game-logcat` picks it up with everything else. Prefixing each
// line with the source name keeps a plugin's output findable in that stream.

using System;
using System.Collections.Generic;

namespace BepInEx.Logging
{
    [Flags]
    public enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warning = 4,
        Message = 8,
        Info = 16,
        Debug = 32,
        All = Fatal | Error | Warning | Message | Info | Debug,
    }

    public class LogEventArgs : EventArgs
    {
        public object Data { get; protected set; }
        public LogLevel Level { get; protected set; }
        public ILogSource Source { get; protected set; }

        public LogEventArgs(object data, LogLevel level, ILogSource source)
        {
            Data = data;
            Level = level;
            Source = source;
        }

        public override string ToString()
        {
            return "[" + Level + ":" + Source.SourceName + "] " + Data;
        }
    }

    public interface ILogSource : IDisposable
    {
        string SourceName { get; }
        event EventHandler<LogEventArgs> LogEvent;
    }

    public class ManualLogSource : ILogSource
    {
        public string SourceName { get; protected set; }

        public event EventHandler<LogEventArgs> LogEvent;

        public ManualLogSource(string sourceName)
        {
            SourceName = sourceName;
        }

        public void Log(LogLevel level, object data)
        {
            var handler = LogEvent;
            if (handler != null) handler(this, new LogEventArgs(data, level, this));

            var line = "[" + SourceName + "] " + data;
            if (level == LogLevel.Error || level == LogLevel.Fatal) UnityEngine.Debug.LogError(line);
            else if (level == LogLevel.Warning) UnityEngine.Debug.LogWarning(line);
            else UnityEngine.Debug.Log(line);
        }

        public void LogFatal(object data) { Log(LogLevel.Fatal, data); }
        public void LogError(object data) { Log(LogLevel.Error, data); }
        public void LogWarning(object data) { Log(LogLevel.Warning, data); }
        public void LogMessage(object data) { Log(LogLevel.Message, data); }
        public void LogInfo(object data) { Log(LogLevel.Info, data); }
        public void LogDebug(object data) { Log(LogLevel.Debug, data); }

        public void Dispose() { }
    }

    public static class Logger
    {
        public static ICollection<ILogSource> Sources { get { return _sources; } }
        static readonly List<ILogSource> _sources = new List<ILogSource>();

        /// <summary>
        /// Listeners exist so that plugins which register one do not crash.
        /// Nothing is dispatched to them beyond what the source itself raises.
        /// </summary>
        public static ICollection<object> Listeners { get { return _listeners; } }
        static readonly List<object> _listeners = new List<object>();

        public static ManualLogSource CreateLogSource(string sourceName)
        {
            var source = new ManualLogSource(sourceName);
            _sources.Add(source);
            return source;
        }

        public static void Log(LogLevel level, object data)
        {
            _fallback.Log(level, data);
        }

        static readonly ManualLogSource _fallback = new ManualLogSource("BepInEx");
    }
}
