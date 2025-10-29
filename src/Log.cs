using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;

public enum LogType
{
    Transitions,
    Timing,
    General,
    Infection
}

public enum CSVLogType
{
    Age1,
    Mortality,
    State
}

public static class Log
{
    private static readonly object InitGate = new object();
    private static volatile bool _initialized;

    private static BlockingCollection<LogItem> _queue;
    private static BlockingCollection<CsvLogItem> _csvQueue;
    private static Task _pump;
    private static Task _csvPump;
    private static CancellationTokenSource _cts;

    private static StreamWriter _globalWriter;
    private static Dictionary<LogType, StreamWriter> _writers;
    private static Dictionary<CSVLogType, StreamWriter> _csvWriters;

    private static string _timestampForFiles = "";
    private static string _logsDir = "logs";
    private static string _globalPath = "";
    private static Dictionary<LogType, string> _paths;
    private static Dictionary<CSVLogType, string> _csvPaths;

    public static string GlobalPath { get { return _globalPath; } }
    public static string GetPath(LogType type)
    {
        if (_paths != null && _paths.ContainsKey(type)) return _paths[type];
        return "";
    }

    public static string GetCsvPath(CSVLogType type)
    {
        if (_csvPaths != null && _csvPaths.ContainsKey(type)) return _csvPaths[type];
        return "";
    }

    /// <summary>
    /// Initialize the logger. Creates ./logs if needed and opens:
    ///   log_all_<ts>.txt and one per LogType with the same <ts>.
    /// </summary>
    public static void Init(string logsDir = "logs")
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;

            _logsDir = string.IsNullOrEmpty(logsDir) ? "logs" : logsDir;
            Directory.CreateDirectory(_logsDir);

            // Filename-safe shared timestamp (UTC) — no ':' for Windows
            // Example: 2025-10-14T170623.123Z
            _timestampForFiles = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'");

            _paths = new Dictionary<LogType, string>();
            _writers = new Dictionary<LogType, StreamWriter>();
            _csvPaths = new Dictionary<CSVLogType, string>();
            _csvWriters = new Dictionary<CSVLogType, StreamWriter>();

            _cts = new CancellationTokenSource();

            // Create the global file first
            _globalPath = Path.Combine(_logsDir, "log_" + _timestampForFiles + "_all.txt");
            _globalWriter = CreateWriter(_globalPath);

            // Create one file per enum value, same timestamp
            foreach (LogType lt in (LogType[])Enum.GetValues(typeof(LogType)))
            {
                string typeLower = lt.ToString().ToLowerInvariant();
                string path = Path.Combine(_logsDir, "log_" + _timestampForFiles + "_" + typeLower + ".txt");
                _paths[lt] = path;
                _writers[lt] = CreateWriter(path);
            }

            foreach (CSVLogType ct in (CSVLogType[])Enum.GetValues(typeof(CSVLogType)))
            {
                string typeLower = ct.ToString().ToLowerInvariant();
                string path = Path.Combine(_logsDir, "log_" + _timestampForFiles + "_" + typeLower + ".csv");
                _csvPaths[ct] = path;
                var sw = CreateWriter(path);
                _csvWriters[ct] = sw;
                if (ct == CSVLogType.Age1)
                    sw.WriteLine("Timestep,Species,Age");
                if (ct == CSVLogType.Mortality)
                    sw.WriteLine("Timestep,Species,Age");
                if (ct == CSVLogType.State)
                    sw.WriteLine("Timestep,Species,Age");
            }

            _queue = new BlockingCollection<LogItem>(new ConcurrentQueue<LogItem>());
            _csvQueue = new BlockingCollection<CsvLogItem>(new ConcurrentQueue<CsvLogItem>());

            // Single writer task that fans out to the type-specific writer and global writer
            _pump = Task.Run(() =>
            {
                try
                {
                    foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
                    {
                        try
                        {
                            // Write to specific log
                            StreamWriter sw;
                            if (_writers != null && _writers.TryGetValue(item.Type, out sw) && sw != null)
                                sw.WriteLine(item.Line);
                            // Always write to global
                            if (_globalWriter != null)
                                _globalWriter.WriteLine(item.Line);
                        }
                        catch
                        {
                            // Optionally route errors elsewhere
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal shutdown
                }
                catch
                {
                    // optionally handle fatal errors
                }
            });

            _csvPump = Task.Run(() =>
            {
                try
                {
                    foreach (var item in _csvQueue.GetConsumingEnumerable(_cts.Token))
                    {
                        try
                        {
                            StreamWriter sw;
                            if (_csvWriters != null && _csvWriters.TryGetValue(item.Type, out sw) && sw != null)
                                sw.WriteLine(item.Line);
                        }
                        catch
                        {
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            });

            // Auto-shutdown
            AppDomain.CurrentDomain.ProcessExit += (object s, EventArgs e) => Shutdown();
            Console.CancelKeyPress += (object s, ConsoleCancelEventArgs e) => Shutdown();

            _initialized = true;
        }
    }

    public static void WriteCSV(CSVLogType type, string message) {
        if (!_initialized)
            Init(); // default ./logs
        _csvQueue.Add(new CsvLogItem { Type = type, Line = message });
    }

    /// <summary>
    /// Log a message to the given type; line also goes to the global file.
    /// RFC 3339 (UTC) with milliseconds + 'Z'.
    /// </summary>
    public static void Write(LogType type, string message)
    {
        if (!_initialized)
            Init(); // default ./logs

        string ts = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        int pid = Process.GetCurrentProcess().Id;
        int tid = Environment.CurrentManagedThreadId;

        var line = "[" + ts + "] [P" + pid + ":T" + tid + "] " + message;

        _queue.Add(new LogItem { Type = type, Line = line });
    }
    public static void MortalityCSV(int timestep, string species, ushort age) { WriteCSV(CSVLogType.Mortality, $"{timestep},{species},{age}"); }
    public static void StateCSV(int timestep, string species, ushort age) { WriteCSV(CSVLogType.State, $"{timestep},{species},{age}"); }
    public static void Age1CSV(int timestep, string species, ushort age) { WriteCSV(CSVLogType.Age1, $"{timestep},{species},{age}"); }
    public static void Info(LogType type, string msg)  { Write(type, "INFO  " + msg); }
    public static void Warn(LogType type, string msg)  { Write(type, "WARN  " + msg); }
    public static void Error(LogType type, string msg) { Write(type, "ERROR " + msg); }

    /// <summary>
    /// Flush and close all files. Idempotent.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized) return;
        lock (InitGate)
        {
            if (!_initialized) return;
            _initialized = false;

            try { if (_queue != null) _queue.CompleteAdding(); } catch { }
            try { if (_csvQueue != null) _csvQueue.CompleteAdding(); } catch { }
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_pump != null) _pump.Wait(2000); } catch { }
            try { if (_csvPump != null) _csvPump.Wait(2000); } catch { }

            // Flush & close per-type writers
            if (_writers != null)
            {
                foreach (var kv in _writers)
                {
                    try { if (kv.Value != null) kv.Value.Flush(); } catch { }
                    try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
                }
            }

            if (_csvWriters != null)
            {
                foreach (var kv in _csvWriters)
                {
                    try { if (kv.Value != null) kv.Value.Flush(); } catch { }
                    try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
                }
            }

            // Flush & close global writer
            try { if (_globalWriter != null) _globalWriter.Flush(); } catch { }
            try { if (_globalWriter != null) _globalWriter.Dispose(); } catch { }

            try { if (_cts != null) _cts.Dispose(); } catch { }

            _queue = null;
            _csvQueue = null;
            _pump = null;
            _csvPump = null;
            _cts = null;
            _writers = null;
            _csvWriters = null;
            _paths = null;
            _csvPaths = null;
            _globalWriter = null;
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        // CreateNew ensures we don’t accidentally overwrite; FileShare.Read so editors can open it
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        return sw;
    }

    private struct LogItem
    {
        public LogType Type;
        public string Line;
    }

    private struct CsvLogItem
    {
        public CSVLogType Type;
        public string Line;
    }
}