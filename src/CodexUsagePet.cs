using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace CodexUsagePet
{
    internal sealed class UsageSnapshot
    {
        public int? FiveHourRemainingPercent;
        public long? FiveHourResetsAt;
        public int? WeekRemainingPercent;
        public long? WeekResetsAt;
        public long? Tokens;
        public string TokenLabel;
        public string Source;
        public DateTime UpdatedAt;

        public UsageSnapshot Clone()
        {
            return new UsageSnapshot
            {
                FiveHourRemainingPercent = FiveHourRemainingPercent,
                FiveHourResetsAt = FiveHourResetsAt,
                WeekRemainingPercent = WeekRemainingPercent,
                WeekResetsAt = WeekResetsAt,
                Tokens = Tokens,
                TokenLabel = TokenLabel,
                Source = Source,
                UpdatedAt = UpdatedAt
            };
        }
    }

    internal sealed class UsageService : IDisposable
    {
        private const string InitializeRequest = "initialize";
        private const string RateLimitsRequest = "rateLimits";
        private const string TokenUsageRequest = "tokenUsage";

        private readonly object gate = new object();
        private readonly object writeGate = new object();
        private readonly Action<UsageSnapshot> onChanged;
        private readonly Dictionary<long, string> pending = new Dictionary<long, string>();
        private UsageSnapshot snapshot = new UsageSnapshot
        {
            TokenLabel = "TODAY TOKENS",
            Source = "CONNECTING",
            UpdatedAt = DateTime.Now
        };
        private Process process;
        private Timer initializeTimer;
        private long nextRequestId;
        private bool ready;
        private bool fallback;
        private bool disposed;
        private bool pathCandidateAttempted;
        private int scanRunning;

        public UsageService(Action<UsageSnapshot> changed)
        {
            onChanged = changed;
        }

        public void Start()
        {
            string preferred = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "plugins", ".plugin-appserver", "codex.exe");

            if (File.Exists(preferred))
            {
                if (Launch(preferred))
                    return;
            }

            pathCandidateAttempted = true;
            if (!Launch("codex"))
                ActivateFallback();
        }

        public void Refresh()
        {
            bool useServer;
            bool useFallback;
            lock (gate)
            {
                useServer = ready && process != null;
                useFallback = fallback;
            }

            if (useServer)
            {
                SendReadRequests();
            }
            else if (useFallback)
            {
                QueueSessionScan();
            }
        }

        private bool Launch(string executable)
        {
            Process candidate = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "app-server",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                candidate = new Process();
                candidate.StartInfo = startInfo;
                candidate.EnableRaisingEvents = true;
                candidate.Exited += delegate { HandleServerFailure(candidate); };

                lock (gate)
                {
                    if (disposed)
                        return false;
                    process = candidate;
                    ready = false;
                    fallback = false;
                    pending.Clear();
                }

                if (!candidate.Start())
                    throw new InvalidOperationException("Codex app-server did not start.");

                StartReaderThread(candidate, false);
                StartReaderThread(candidate, true);
                SendInitialize(candidate);

                initializeTimer = new Timer(
                    delegate { HandleInitializeTimeout(candidate); },
                    null,
                    7000,
                    Timeout.Infinite);
                return true;
            }
            catch
            {
                lock (gate)
                {
                    if (ReferenceEquals(process, candidate))
                        process = null;
                }
                TryStop(candidate);
                return false;
            }
        }

        private void StartReaderThread(Process owner, bool standardError)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    StreamReader reader = standardError ? owner.StandardError : owner.StandardOutput;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!standardError)
                            HandleServerLine(owner, line);
                    }
                }
                catch
                {
                    // Process exit and disposal both close the redirected streams.
                }
            });
            thread.IsBackground = true;
            thread.Name = standardError ? "CodexUsagePet stderr" : "CodexUsagePet JSONL";
            thread.Start();
        }

        private void SendInitialize(Process owner)
        {
            long id = Interlocked.Increment(ref nextRequestId);
            var clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codex-usage-pet";
            clientInfo["title"] = "Codex Usage Pet";
            clientInfo["version"] = "0.1.0";

            var capabilities = new Dictionary<string, object>();
            capabilities["experimentalApi"] = true;

            var parameters = new Dictionary<string, object>();
            parameters["clientInfo"] = clientInfo;
            parameters["capabilities"] = capabilities;

            var request = new Dictionary<string, object>();
            request["id"] = id;
            request["method"] = "initialize";
            request["params"] = parameters;

            lock (gate)
                pending[id] = InitializeRequest;
            Send(owner, request);
        }

        private void SendReadRequests()
        {
            Process owner;
            lock (gate)
                owner = process;
            if (owner == null)
                return;

            SendRequest(owner, "account/rateLimits/read", RateLimitsRequest);
            SendRequest(owner, "account/usage/read", TokenUsageRequest);
        }

        private void SendRequest(Process owner, string method, string kind)
        {
            long id = Interlocked.Increment(ref nextRequestId);
            var request = new Dictionary<string, object>();
            request["id"] = id;
            request["method"] = method;
            lock (gate)
                pending[id] = kind;
            Send(owner, request);
        }

        private void Send(Process owner, Dictionary<string, object> request)
        {
            try
            {
                string json = new JavaScriptSerializer().Serialize(request);
                lock (writeGate)
                {
                    lock (gate)
                    {
                        if (disposed || !ReferenceEquals(process, owner))
                            return;
                    }
                    owner.StandardInput.WriteLine(json);
                    owner.StandardInput.Flush();
                }
            }
            catch
            {
                HandleServerFailure(owner);
            }
        }

        private void HandleServerLine(Process owner, string line)
        {
            Dictionary<string, object> message;
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 8 * 1024 * 1024;
                message = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                return;
            }

            if (message == null)
                return;

            object methodValue;
            if (message.TryGetValue("method", out methodValue))
            {
                string method = methodValue as string;
                if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
                {
                    Dictionary<string, object> parameters = GetDictionary(message, "params");
                    Dictionary<string, object> limits = GetDictionary(parameters, "rateLimits");
                    if (limits != null)
                        ApplyRateLimits(limits);
                }
                return;
            }

            long id;
            if (!TryGetLong(message, "id", out id))
                return;

            string kind;
            lock (gate)
            {
                if (!ReferenceEquals(process, owner) || !pending.TryGetValue(id, out kind))
                    return;
                pending.Remove(id);
            }

            if (message.ContainsKey("error"))
            {
                if (kind == InitializeRequest)
                    HandleServerFailure(owner);
                return;
            }

            Dictionary<string, object> result = GetDictionary(message, "result");
            if (kind == InitializeRequest)
            {
                lock (gate)
                {
                    if (!ReferenceEquals(process, owner) || disposed)
                        return;
                    ready = true;
                    pathCandidateAttempted = true;
                    fallback = false;
                    snapshot.Source = "APP SERVER";
                    snapshot.UpdatedAt = DateTime.Now;
                }
                DisposeInitializeTimer();
                Publish();
                SendInitialized(owner);
                SendReadRequests();
            }
            else if (kind == RateLimitsRequest && result != null)
            {
                Dictionary<string, object> limits = SelectRateLimitSnapshot(result);
                if (limits != null)
                    ApplyRateLimits(limits);
            }
            else if (kind == TokenUsageRequest && result != null)
            {
                ApplyTokenUsage(result);
            }
        }

        private void SendInitialized(Process owner)
        {
            var notification = new Dictionary<string, object>();
            notification["method"] = "initialized";
            Send(owner, notification);
        }

        private Dictionary<string, object> SelectRateLimitSnapshot(Dictionary<string, object> result)
        {
            Dictionary<string, object> limits = GetDictionary(result, "rateLimits");
            if (limits != null)
                return limits;

            Dictionary<string, object> byId = GetDictionary(result, "rateLimitsByLimitId");
            if (byId == null)
                return null;

            Dictionary<string, object> codex = GetDictionary(byId, "codex");
            if (codex != null)
                return codex;
            foreach (KeyValuePair<string, object> pair in byId)
            {
                Dictionary<string, object> candidate = pair.Value as Dictionary<string, object>;
                if (candidate != null)
                    return candidate;
            }
            return null;
        }

        private void ApplyRateLimits(Dictionary<string, object> limits)
        {
            lock (gate)
            {
                ApplyWindow(GetDictionary(limits, "primary"), true, snapshot);
                ApplyWindow(GetDictionary(limits, "secondary"), false, snapshot);
                snapshot.Source = "APP SERVER";
                snapshot.UpdatedAt = DateTime.Now;
            }
            Publish();
        }

        private static void ApplyWindow(
            Dictionary<string, object> window,
            bool primary,
            UsageSnapshot target)
        {
            if (window == null)
            {
                if (primary)
                {
                    target.FiveHourRemainingPercent = null;
                    target.FiveHourResetsAt = null;
                }
                else
                {
                    target.WeekRemainingPercent = null;
                    target.WeekResetsAt = null;
                }
                return;
            }

            long remaining;
            int? percent = null;
            if (TryGetLongEither(window, "remainingPercent", "remaining_percent", out remaining))
            {
                percent = ClampPercent(remaining);
            }
            else
            {
                long used;
                if (TryGetLongEither(window, "usedPercent", "used_percent", out used))
                    percent = RemainingPercentFromUsed(used);
            }

            long resetValue;
            long? reset = TryGetLongEither(window, "resetsAt", "resets_at", out resetValue)
                ? (long?)resetValue
                : null;
            if (primary)
            {
                target.FiveHourRemainingPercent = percent;
                target.FiveHourResetsAt = reset;
            }
            else
            {
                target.WeekRemainingPercent = percent;
                target.WeekResetsAt = reset;
            }
        }

        private static int ClampPercent(long value)
        {
            if (value <= 0L)
                return 0;
            if (value >= 100L)
                return 100;
            return (int)value;
        }

        private static int RemainingPercentFromUsed(long used)
        {
            if (used <= 0L)
                return 100;
            if (used >= 100L)
                return 0;
            return 100 - (int)used;
        }

        private void ApplyTokenUsage(Dictionary<string, object> result)
        {
            long? todayTokens = null;
            object bucketsValue;
            if (result.TryGetValue("dailyUsageBuckets", out bucketsValue))
            {
                foreach (object value in AsEnumerable(bucketsValue))
                {
                    Dictionary<string, object> bucket = value as Dictionary<string, object>;
                    if (bucket == null)
                        continue;

                    object startValue;
                    long tokens;
                    if (!bucket.TryGetValue("startDate", out startValue) ||
                        !TryGetLong(bucket, "tokens", out tokens))
                        continue;

                    DateTime startDate;
                    if (DateTime.TryParse(
                        Convert.ToString(startValue, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out startDate) &&
                        (startDate.Date == DateTime.Today || startDate.Date == DateTime.UtcNow.Date))
                    {
                        todayTokens = (todayTokens ?? 0L) + Math.Max(0L, tokens);
                    }
                }
            }

            lock (gate)
            {
                if (todayTokens.HasValue)
                {
                    snapshot.Tokens = todayTokens;
                    snapshot.TokenLabel = "TODAY TOKENS";
                }
                snapshot.Source = "APP SERVER";
                snapshot.UpdatedAt = DateTime.Now;
            }
            Publish();
        }

        private void HandleInitializeTimeout(Process owner)
        {
            lock (gate)
            {
                if (ready || disposed || !ReferenceEquals(process, owner))
                    return;
            }
            HandleServerFailure(owner);
        }

        private void HandleServerFailure(Process owner)
        {
            bool tryPath = false;
            lock (gate)
            {
                if (disposed || !ReferenceEquals(process, owner))
                    return;
                process = null;
                ready = false;
                pending.Clear();
                if (!pathCandidateAttempted)
                {
                    pathCandidateAttempted = true;
                    tryPath = true;
                }
            }

            DisposeInitializeTimer();
            TryStop(owner);
            if (tryPath && Launch("codex"))
                return;
            ActivateFallback();
        }

        private void ActivateFallback()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                fallback = true;
                ready = false;
                snapshot.Source = "SESSION FALLBACK";
                snapshot.UpdatedAt = DateTime.Now;
            }
            Publish();
            QueueSessionScan();
        }

        private void QueueSessionScan()
        {
            if (Interlocked.CompareExchange(ref scanRunning, 1, 0) != 0)
                return;

            Thread thread = new Thread(delegate()
            {
                try
                {
                    UsageSnapshot scanned = ScanLatestSession();
                    if (scanned != null)
                    {
                        lock (gate)
                        {
                            if (!fallback || disposed)
                                return;
                            snapshot.FiveHourRemainingPercent = scanned.FiveHourRemainingPercent;
                            snapshot.FiveHourResetsAt = scanned.FiveHourResetsAt;
                            snapshot.WeekRemainingPercent = scanned.WeekRemainingPercent;
                            snapshot.WeekResetsAt = scanned.WeekResetsAt;
                            snapshot.Tokens = scanned.Tokens;
                            snapshot.TokenLabel = "THREAD TOKENS";
                            snapshot.Source = "SESSION FALLBACK";
                            snapshot.UpdatedAt = DateTime.Now;
                        }
                        Publish();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref scanRunning, 0);
                }
            });
            thread.IsBackground = true;
            thread.Name = "CodexUsagePet session scan";
            thread.Start();
        }

        private static UsageSnapshot ScanLatestSession()
        {
            string codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var files = new List<FileInfo>();
            CollectJsonlFiles(Path.Combine(codexHome, "sessions"), files);
            CollectJsonlFiles(Path.Combine(codexHome, "archived_sessions"), files);

            foreach (FileInfo file in files.OrderByDescending(delegate(FileInfo value)
            {
                return value.LastWriteTimeUtc;
            }))
            {
                UsageSnapshot parsed = ScanTokenCountRecords(file.FullName);
                if (parsed != null)
                    return parsed;
            }
            return null;
        }

        private static void CollectJsonlFiles(string root, List<FileInfo> output)
        {
            if (!Directory.Exists(root))
                return;

            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                try
                {
                    foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
                    {
                        try { output.Add(new FileInfo(file)); }
                        catch { }
                    }
                    foreach (string child in Directory.GetDirectories(directory))
                        directories.Push(child);
                }
                catch
                {
                    // A single inaccessible directory must not stop the read-only fallback.
                }
            }
        }

        private static UsageSnapshot ScanTokenCountRecords(string path)
        {
            UsageSnapshot latest = null;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length > 4 * 1024 * 1024 ||
                        line.IndexOf("\"token_count\"", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    Dictionary<string, object> record;
                    try
                    {
                        var serializer = new JavaScriptSerializer();
                        serializer.MaxJsonLength = 4 * 1024 * 1024;
                        record = serializer.DeserializeObject(line) as Dictionary<string, object>;
                    }
                    catch
                    {
                        continue;
                    }

                    if (record == null || !IsTokenCountRecord(record, 0))
                        continue;

                    long total = 0;
                    Dictionary<string, object> totalUsage;
                    bool foundTotal =
                        FindDictionaryByKey(
                            record,
                            "total_token_usage",
                            "totalTokenUsage",
                            0,
                            out totalUsage) &&
                        TryGetLongEither(totalUsage, "total_tokens", "totalTokens", out total);
                    if (!foundTotal &&
                        !FindNumberByKey(record, "total_tokens", "totalTokens", 0, out total))
                        continue;

                    var current = new UsageSnapshot();
                    current.Tokens = Math.Max(0L, total);
                    Dictionary<string, object> rateLimits;
                    if (FindDictionaryByKey(record, "rate_limits", "rateLimits", 0, out rateLimits))
                    {
                        ApplyWindow(GetDictionaryEither(rateLimits, "primary", "primary"), true, current);
                        ApplyWindow(GetDictionaryEither(rateLimits, "secondary", "secondary"), false, current);
                    }
                    latest = current;
                }
            }
            catch
            {
                return null;
            }
            return latest;
        }

        private static bool IsTokenCountRecord(object value, int depth)
        {
            if (value == null || depth > 9)
                return false;
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (string.Equals(pair.Key, "type", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.Value as string, "token_count", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (IsTokenCountRecord(pair.Value, depth + 1))
                        return true;
                }
            }
            else
            {
                foreach (object item in AsEnumerable(value))
                {
                    if (IsTokenCountRecord(item, depth + 1))
                        return true;
                }
            }
            return false;
        }

        private static bool FindNumberByKey(
            object value,
            string firstKey,
            string secondKey,
            int depth,
            out long number)
        {
            number = 0;
            if (value == null || depth > 12)
                return false;
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if ((string.Equals(pair.Key, firstKey, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(pair.Key, secondKey, StringComparison.OrdinalIgnoreCase)) &&
                        TryConvertLong(pair.Value, out number))
                        return true;
                }
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (FindNumberByKey(pair.Value, firstKey, secondKey, depth + 1, out number))
                        return true;
                }
            }
            else
            {
                foreach (object item in AsEnumerable(value))
                {
                    if (FindNumberByKey(item, firstKey, secondKey, depth + 1, out number))
                        return true;
                }
            }
            return false;
        }

        private static bool FindDictionaryByKey(
            object value,
            string firstKey,
            string secondKey,
            int depth,
            out Dictionary<string, object> found)
        {
            found = null;
            if (value == null || depth > 12)
                return false;
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (string.Equals(pair.Key, firstKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pair.Key, secondKey, StringComparison.OrdinalIgnoreCase))
                    {
                        found = pair.Value as Dictionary<string, object>;
                        if (found != null)
                            return true;
                    }
                }
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (FindDictionaryByKey(pair.Value, firstKey, secondKey, depth + 1, out found))
                        return true;
                }
            }
            else
            {
                foreach (object item in AsEnumerable(value))
                {
                    if (FindDictionaryByKey(item, firstKey, secondKey, depth + 1, out found))
                        return true;
                }
            }
            return false;
        }

        private void Publish()
        {
            UsageSnapshot copy;
            lock (gate)
                copy = snapshot.Clone();
            if (onChanged != null)
                onChanged(copy);
        }

        private void DisposeInitializeTimer()
        {
            Timer timer = Interlocked.Exchange(ref initializeTimer, null);
            if (timer != null)
                timer.Dispose();
        }

        private static void TryStop(Process owner)
        {
            if (owner == null)
                return;
            try { owner.StandardInput.Close(); }
            catch { }
            try
            {
                if (!owner.HasExited)
                    owner.Kill();
            }
            catch { }
            try { owner.Dispose(); }
            catch { }
        }

        public void Dispose()
        {
            Process owner;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                owner = process;
                process = null;
                pending.Clear();
            }
            DisposeInitializeTimer();
            TryStop(owner);
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> parent,
            string key)
        {
            if (parent == null)
                return null;
            object value;
            if (!parent.TryGetValue(key, out value))
                return null;
            return value as Dictionary<string, object>;
        }

        private static Dictionary<string, object> GetDictionaryEither(
            Dictionary<string, object> parent,
            string firstKey,
            string secondKey)
        {
            Dictionary<string, object> value = GetDictionary(parent, firstKey);
            return value ?? GetDictionary(parent, secondKey);
        }

        private static bool TryGetLong(Dictionary<string, object> dictionary, string key, out long value)
        {
            value = 0;
            if (dictionary == null)
                return false;
            object raw;
            return dictionary.TryGetValue(key, out raw) && TryConvertLong(raw, out value);
        }

        private static bool TryGetLongEither(
            Dictionary<string, object> dictionary,
            string firstKey,
            string secondKey,
            out long value)
        {
            return TryGetLong(dictionary, firstKey, out value) ||
                   TryGetLong(dictionary, secondKey, out value);
        }

        private static bool TryConvertLong(object raw, out long value)
        {
            value = 0;
            if (raw == null || raw is bool || raw is string)
                return false;
            try
            {
                value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<object> AsEnumerable(object value)
        {
            object[] array = value as object[];
            if (array != null)
                return array;
            ArrayList list = value as ArrayList;
            if (list != null)
                return list.Cast<object>();
            return new object[0];
        }
    }

    internal sealed class DetailsWindow : Window
    {
        private readonly TextBlock fiveHourValue;
        private readonly TextBlock fiveHourReset;
        private readonly TextBlock weekValue;
        private readonly TextBlock weekReset;
        private readonly TextBlock tokenValue;
        private readonly Ellipse statusDot;

        public DetailsWindow()
        {
            Width = 250;
            Height = 150;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SnapsToDevicePixels = true;

            Border card = new Border();
            card.Background = MakeBrush(12, 14, 17, 252);
            card.BorderBrush = MakeBrush(232, 235, 239, 235);
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(14, 10, 14, 9);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Text("B TAIL METER", 13, FontWeights.SemiBold, MakeBrush(244, 246, 248, 255));
            title.LetterSpacingCompat(1.0);
            heading.Children.Add(title);
            statusDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = MakeBrush(128, 135, 143, 255),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(statusDot, 1);
            heading.Children.Add(statusDot);
            Grid.SetRow(heading, 0);
            content.Children.Add(heading);

            fiveHourValue = AddRow(content, 1, "5H LEFT", "--");
            fiveHourReset = AddReset(content, 2);
            weekValue = AddRow(content, 3, "WEEK LEFT", "--");
            weekReset = AddReset(content, 4);
            tokenValue = AddRow(content, 5, "TODAY TOKENS", "--");

            card.Child = content;
            Content = card;
        }

        private static TextBlock AddRow(Grid parent, int row, string label, string value)
        {
            Grid line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock left = Text(label, 12, FontWeights.Normal, MakeBrush(205, 210, 216, 255));
            TextBlock right = Text(value, 13, FontWeights.SemiBold, MakeBrush(53, 227, 139, 255));
            right.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(right, 1);
            line.Children.Add(left);
            line.Children.Add(right);
            Grid.SetRow(line, row);
            parent.Children.Add(line);
            return right;
        }

        private static TextBlock AddReset(Grid parent, int row)
        {
            TextBlock reset = Text("RESET --", 10, FontWeights.Normal, MakeBrush(132, 139, 147, 255));
            reset.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(reset, row);
            parent.Children.Add(reset);
            return reset;
        }

        private static TextBlock Text(string value, double size, FontWeight weight, Brush color)
        {
            return new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Consolas"),
                FontSize = size,
                FontWeight = weight,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public void Update(UsageSnapshot value)
        {
            fiveHourValue.Text = FormatPercent(value.FiveHourRemainingPercent);
            fiveHourValue.Foreground = RemainingBrush(value.FiveHourRemainingPercent);
            fiveHourReset.Text = "RESET " + FormatReset(value.FiveHourResetsAt);
            weekValue.Text = FormatPercent(value.WeekRemainingPercent);
            weekValue.Foreground = RemainingBrush(value.WeekRemainingPercent);
            weekReset.Text = "RESET " + FormatReset(value.WeekResetsAt);
            tokenValue.Text = FormatTokens(value.Tokens);

            string label = string.IsNullOrEmpty(value.TokenLabel) ? "TOKENS" : value.TokenLabel;
            Grid row = tokenValue.Parent as Grid;
            if (row != null && row.Children.Count > 0)
            {
                TextBlock labelText = row.Children[0] as TextBlock;
                if (labelText != null)
                    labelText.Text = label;
            }

            statusDot.Fill = value.Source == "APP SERVER"
                ? MakeBrush(53, 227, 139, 255)
                : value.Source == "SESSION FALLBACK"
                    ? MakeBrush(255, 170, 59, 255)
                    : MakeBrush(128, 135, 143, 255);
        }

        internal static string FormatPercent(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        }

        internal static Brush RemainingBrush(int? percent)
        {
            if (!percent.HasValue)
                return MakeBrush(128, 135, 143, 255);
            if (percent.Value >= 70)
                return MakeBrush(53, 227, 139, 255);
            if (percent.Value >= 30)
                return MakeBrush(255, 170, 59, 255);
            return MakeBrush(255, 77, 90, 255);
        }

        internal static string FormatReset(long? epochSeconds)
        {
            if (!epochSeconds.HasValue)
                return "--";
            DateTime resetUtc;
            try
            {
                resetUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(epochSeconds.Value);
            }
            catch
            {
                return "--";
            }

            TimeSpan remaining = resetUtc - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0)
                return "NOW";
            if (remaining.TotalDays >= 1)
                return ((int)remaining.TotalDays).ToString(CultureInfo.InvariantCulture) + "D " +
                       remaining.Hours.ToString(CultureInfo.InvariantCulture) + "H";
            if (remaining.TotalHours >= 1)
                return ((int)remaining.TotalHours).ToString(CultureInfo.InvariantCulture) + "H " +
                       remaining.Minutes.ToString(CultureInfo.InvariantCulture) + "M";
            return Math.Max(1, remaining.Minutes).ToString(CultureInfo.InvariantCulture) + "M";
        }

        internal static string FormatTokens(long? value)
        {
            if (!value.HasValue)
                return "--";
            double number = value.Value;
            if (number >= 1000000000d)
                return (number / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (number >= 1000000d)
                return (number / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (number >= 1000d)
                return (number / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return value.Value.ToString("N0", CultureInfo.InvariantCulture);
        }

        internal static SolidColorBrush MakeBrush(byte red, byte green, byte blue, byte alpha)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }
    }

    internal static class TextBlockCompatibility
    {
        public static void LetterSpacingCompat(this TextBlock text, double ignored)
        {
            // WPF on .NET Framework has no letter-spacing property; the monospaced face
            // preserves the compact meter look without a custom text renderer.
        }
    }

    internal sealed class PetWindow : Window
    {
        private const double BasePetWidth = 170.0;
        private const double BasePetHeight = 150.0;
        private const double MinimumPetScale = 0.60;
        private const double MaximumPetScale = 1.80;

        private readonly List<Ellipse> progressDots = new List<Ellipse>();
        private readonly List<MenuItem> scaleMenuItems = new List<MenuItem>();
        private readonly TextBlock percentText;
        private readonly Border percentBadge;
        private readonly DetailsWindow details;
        private readonly UsageService usageService;
        private readonly DispatcherTimer refreshTimer;
        private readonly string positionPath;
        private UsageSnapshot current = new UsageSnapshot();
        private Drawing.Point dragStartCursor;
        private double dragStartLeft;
        private double dragStartTop;
        private bool mouseDown;
        private bool dragged;
        private bool exiting;
        private double petScale = 1.0;

        public PetWindow()
        {
            Width = BasePetWidth;
            Height = BasePetHeight;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            SnapsToDevicePixels = true;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Title = "Codex Usage Pet";

            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            positionPath = Path.Combine(localData, "CodexUsagePet", "position.txt");

            Canvas root = new Canvas
            {
                Width = Width,
                Height = Height,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            System.Windows.Controls.Image mascot = new System.Windows.Controls.Image
            {
                Width = 150,
                Height = 138,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Source = LoadMascot()
            };
            RenderOptions.SetBitmapScalingMode(mascot, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(mascot, 20);
            Canvas.SetTop(mascot, 8);
            root.Children.Add(mascot);

            percentBadge = new Border
            {
                MinWidth = 38,
                Height = 24,
                Background = DetailsWindow.MakeBrush(9, 12, 15, 235),
                BorderBrush = DetailsWindow.MakeBrush(239, 242, 245, 235),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 1, 5, 1)
            };
            percentText = new TextBlock
            {
                Text = "--",
                Foreground = DetailsWindow.MakeBrush(53, 227, 139, 255),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            percentBadge.Child = percentText;
            Canvas.SetLeft(percentBadge, 3);
            Canvas.SetTop(percentBadge, 36);
            root.Children.Add(percentBadge);

            for (int index = 0; index < 7; index++)
            {
                Ellipse dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = DetailsWindow.MakeBrush(75, 83, 91, 210),
                    Stroke = DetailsWindow.MakeBrush(9, 12, 15, 210),
                    StrokeThickness = 0.7
                };
                Canvas.SetLeft(dot, 8 + (index % 2 == 0 ? 0 : 3));
                Canvas.SetTop(dot, 67 + index * 10);
                root.Children.Add(dot);
                progressDots.Add(dot);
            }

            Content = new Viewbox
            {
                Stretch = Stretch.Fill,
                Child = root
            };
            ContextMenu = BuildContextMenu();

            PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
            PreviewMouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
            PreviewMouseWheel += OnPreviewMouseWheel;
            LocationChanged += delegate { PositionDetails(); };
            Closing += OnClosing;

            details = new DetailsWindow();

            LoadPosition();
            usageService = new UsageService(delegate(UsageSnapshot value)
            {
                Dispatcher.BeginInvoke((Action)delegate { ApplySnapshot(value); });
            });
            usageService.Start();

            refreshTimer = new DispatcherTimer(DispatcherPriority.Background);
            refreshTimer.Interval = TimeSpan.FromSeconds(60);
            refreshTimer.Tick += delegate { usageService.Refresh(); };
            refreshTimer.Start();

            System.Windows.Application application = System.Windows.Application.Current;
            if (application != null)
            {
                application.SessionEnding += OnSessionEnding;
                application.Exit += OnApplicationExit;
            }
        }

        private BitmapSource LoadMascot()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "assets", "mascot.png")),
                Path.GetFullPath(Path.Combine(baseDirectory, "assets", "mascot.png")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "assets", "mascot.png"))
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                try
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch { }
            }
            return null;
        }

        private System.Windows.Controls.ContextMenu BuildContextMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                Background = DetailsWindow.MakeBrush(19, 22, 26, 255),
                Foreground = DetailsWindow.MakeBrush(238, 241, 244, 255),
                BorderBrush = DetailsWindow.MakeBrush(94, 101, 109, 255),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12
            };
            MenuItem refresh = new MenuItem { Header = "刷新" };
            refresh.Click += delegate { usageService.Refresh(); };
            MenuItem showDetails = new MenuItem { Header = "显示详情" };
            showDetails.Click += delegate { ShowDetails(); };
            MenuItem sizeMenu = new MenuItem { Header = "宠物大小" };
            double[] presetScales = { 0.75, 1.00, 1.25, 1.50 };
            for (int index = 0; index < presetScales.Length; index++)
            {
                double selectedScale = presetScales[index];
                MenuItem sizeItem = new MenuItem
                {
                    Header = Math.Round(selectedScale * 100.0).ToString(
                        CultureInfo.InvariantCulture) + "%",
                    IsCheckable = true,
                    Tag = selectedScale
                };
                sizeItem.Click += delegate
                {
                    SetPetScale(selectedScale, true, true);
                };
                sizeMenu.Items.Add(sizeItem);
                scaleMenuItems.Add(sizeItem);
            }
            MenuItem reset = new MenuItem { Header = "重置位置" };
            reset.Click += delegate { ResetPosition(); };
            MenuItem exit = new MenuItem { Header = "退出" };
            exit.Click += delegate { Quit(); };
            menu.Items.Add(refresh);
            menu.Items.Add(showDetails);
            menu.Items.Add(sizeMenu);
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            UpdateScaleMenuChecks();
            return menu;
        }

        private void ApplySnapshot(UsageSnapshot value)
        {
            current = value;
            percentText.Text = DetailsWindow.FormatPercent(value.FiveHourRemainingPercent);
            Brush remainingColor = DetailsWindow.RemainingBrush(value.FiveHourRemainingPercent);
            percentText.Foreground = remainingColor;
            percentBadge.BorderBrush = remainingColor;

            int activeDots = value.FiveHourRemainingPercent.HasValue
                ? (int)Math.Ceiling(value.FiveHourRemainingPercent.Value * progressDots.Count / 100.0)
                : 0;
            if (value.FiveHourRemainingPercent.HasValue && value.FiveHourRemainingPercent.Value > 0 && activeDots == 0)
                activeDots = 1;
            for (int index = 0; index < progressDots.Count; index++)
            {
                progressDots[index].Fill = index < activeDots
                    ? remainingColor
                    : DetailsWindow.MakeBrush(75, 83, 91, 210);
            }

            details.Update(value);
            ToolTip = "5h remaining " + DetailsWindow.FormatPercent(value.FiveHourRemainingPercent) +
                      "  ·  week remaining " + DetailsWindow.FormatPercent(value.WeekRemainingPercent) +
                      "  ·  " + DetailsWindow.FormatTokens(value.Tokens);
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || e.Delta == 0)
                return;

            double direction = e.Delta > 0 ? 1.0 : -1.0;
            SetPetScale(petScale + direction * 0.10, true, true);
            e.Handled = true;
        }

        private void SetPetScale(double value, bool persist, bool keepVisible)
        {
            double normalized = Math.Round(
                Clamp(value, MinimumPetScale, MaximumPetScale) * 100.0,
                MidpointRounding.AwayFromZero) / 100.0;
            petScale = normalized;

            MinWidth = 0;
            MinHeight = 0;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            Width = BasePetWidth * petScale;
            Height = BasePetHeight * petScale;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;

            UpdateScaleMenuChecks();
            if (keepVisible)
                KeepWindowVisible();
            PositionDetails();
            if (persist)
                SavePosition();
        }

        private void UpdateScaleMenuChecks()
        {
            foreach (MenuItem item in scaleMenuItems)
            {
                double itemScale = item.Tag is double ? (double)item.Tag : -1.0;
                item.IsChecked = Math.Abs(itemScale - petScale) < 0.001;
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            mouseDown = true;
            dragged = false;
            dragStartCursor = Forms.Control.MousePosition;
            dragStartLeft = Left;
            dragStartTop = Top;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!mouseDown || e.LeftButton != MouseButtonState.Pressed)
                return;

            Drawing.Point cursor = Forms.Control.MousePosition;
            Vector deviceDelta = new Vector(
                cursor.X - dragStartCursor.X,
                cursor.Y - dragStartCursor.Y);
            PresentationSource source = PresentationSource.FromVisual(this);
            Vector logicalDelta = source != null && source.CompositionTarget != null
                ? source.CompositionTarget.TransformFromDevice.Transform(deviceDelta)
                : deviceDelta;

            if (!dragged &&
                (Math.Abs(logicalDelta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(logicalDelta.Y) >= SystemParameters.MinimumVerticalDragDistance))
            {
                dragged = true;
                Cursor = Cursors.SizeAll;
            }

            if (dragged)
            {
                Left = Clamp(
                    dragStartLeft + logicalDelta.X,
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
                Top = Clamp(
                    dragStartTop + logicalDelta.Y,
                    SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
            }
            e.Handled = true;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!mouseDown)
                return;
            mouseDown = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            if (dragged)
            {
                KeepWindowVisible();
                SavePosition();
            }
            else
                ToggleDetails();
            e.Handled = true;
        }

        private void ToggleDetails()
        {
            if (details.IsVisible)
                details.Hide();
            else
                ShowDetails();
        }

        private void ShowDetails()
        {
            details.Update(current);
            PositionDetails();
            details.Show();
        }

        private void PositionDetails()
        {
            if (details == null)
                return;
            Rect area = GetWorkingAreaForWindow();
            double desiredLeft = Left + Width - details.Width;
            double desiredTop = Top - details.Height - 8;
            if (desiredTop < area.Top)
                desiredTop = Top + Height + 8;
            details.Left = Clamp(desiredLeft, area.Left + 4, area.Right - details.Width - 4);
            details.Top = Clamp(desiredTop, area.Top + 4, area.Bottom - details.Height - 4);
        }

        private Rect GetWorkingAreaForWindow()
        {
            double centerX = double.IsNaN(Left) ? 0.0 : Left + Width / 2.0;
            double centerY = double.IsNaN(Top) ? 0.0 : Top + Height / 2.0;
            System.Windows.Point deviceCenter = new System.Windows.Point(centerX, centerY);
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                deviceCenter = source.CompositionTarget.TransformToDevice.Transform(deviceCenter);

            Forms.Screen screen = Forms.Screen.FromPoint(new Drawing.Point(
                (int)Math.Round(deviceCenter.X),
                (int)Math.Round(deviceCenter.Y)));
            Drawing.Rectangle working = screen.WorkingArea;
            System.Windows.Point topLeft = new System.Windows.Point(working.Left, working.Top);
            System.Windows.Point bottomRight = new System.Windows.Point(working.Right, working.Bottom);
            if (source != null && source.CompositionTarget != null)
            {
                topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
                bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
            }
            return new Rect(topLeft, bottomRight);
        }

        private void KeepWindowVisible()
        {
            Rect area = GetWorkingAreaForWindow();
            Left = Clamp(Left, area.Left, area.Right - Width);
            Top = Clamp(Top, area.Top, area.Bottom - Height);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum)
                return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void LoadPosition()
        {
            bool loaded = false;
            try
            {
                string[] lines = File.ReadAllLines(positionPath);
                double storedScale;
                if (lines.Length >= 3 &&
                    double.TryParse(
                        lines[2],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out storedScale) &&
                    !double.IsNaN(storedScale) && !double.IsInfinity(storedScale))
                {
                    // Accept both multiplier-style values (1.25) and percentage-style
                    // values (125) so manually edited settings remain forgiving.
                    if (storedScale > 10.0)
                        storedScale /= 100.0;
                    SetPetScale(storedScale, false, false);
                }

                double left;
                double top;
                if (lines.Length >= 2 &&
                    double.TryParse(lines[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) &&
                    double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top) &&
                    !double.IsNaN(left) && !double.IsInfinity(left) &&
                    !double.IsNaN(top) && !double.IsInfinity(top))
                {
                    Left = left;
                    Top = top;
                    KeepWindowVisible();
                    loaded = true;
                }
            }
            catch { }

            if (!loaded)
                SetDefaultPosition();
        }

        private void SetDefaultPosition()
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Bottom - Height - 18;
        }

        private void ResetPosition()
        {
            SetDefaultPosition();
            SavePosition();
            PositionDetails();
        }

        private void SavePosition()
        {
            try
            {
                string directory = Path.GetDirectoryName(positionPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllLines(positionPath, new[]
                {
                    Left.ToString("R", CultureInfo.InvariantCulture),
                    Top.ToString("R", CultureInfo.InvariantCulture),
                    petScale.ToString("R", CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }

        public void ShowPet()
        {
            if (!IsVisible)
                Show();
            Topmost = true;
            Activate();
        }

        public void HidePet()
        {
            details.Hide();
            Hide();
        }

        public void RefreshUsage()
        {
            usageService.Refresh();
        }

        public void Quit()
        {
            if (exiting)
                return;
            SavePosition();
            exiting = true;
            details.Close();
            Close();
            System.Windows.Application application = System.Windows.Application.Current;
            if (application != null)
                application.Shutdown();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SavePosition();
            if (!exiting)
            {
                e.Cancel = true;
                HidePet();
                return;
            }
            refreshTimer.Stop();
            usageService.Dispose();
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            SavePosition();
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            SavePosition();
            refreshTimer.Stop();
            usageService.Dispose();
        }
    }

    internal sealed class PipeController : IDisposable
    {
        public const string PipeName = "CodexUsagePet.Control.v1";
        private readonly PetWindow window;
        private volatile bool stopping;
        private Thread listener;

        public PipeController(PetWindow petWindow)
        {
            window = petWindow;
        }

        public void Start()
        {
            listener = new Thread(Listen);
            listener.IsBackground = true;
            listener.Name = "CodexUsagePet control pipe";
            listener.Start();
        }

        private void Listen()
        {
            while (!stopping)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8, true, 256, true))
                        {
                            string command = reader.ReadLine();
                            Dispatch(command);
                        }
                    }
                }
                catch
                {
                    if (stopping)
                        return;
                    Thread.Sleep(100);
                }
            }
        }

        private void Dispatch(string command)
        {
            string normalized = string.IsNullOrEmpty(command)
                ? "--show"
                : command.Trim().ToLowerInvariant();
            window.Dispatcher.BeginInvoke((Action)delegate()
            {
                if (normalized == "--hide")
                    window.HidePet();
                else if (normalized == "--refresh")
                    window.RefreshUsage();
                else if (normalized == "--quit")
                    window.Quit();
                else
                    window.ShowPet();
            });
        }

        public static bool Send(string command)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(
                        ".", PipeName, PipeDirection.Out, PipeOptions.None))
                    {
                        client.Connect(200);
                        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 256, true))
                        {
                            writer.WriteLine(command);
                            writer.Flush();
                        }
                        return true;
                    }
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }
            return false;
        }

        public void Dispose()
        {
            stopping = true;
        }
    }

    internal static class Program
    {
        private const string MutexName = "Local\\CodexUsagePet.Singleton.v1";

        [STAThread]
        public static int Main(string[] args)
        {
            string command = ParseCommand(args);
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                    return PipeController.Send(command) ? 0 : 2;

                if (command == "--quit" || command == "--hide")
                    return 0;

                var application = new System.Windows.Application();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = new PetWindow();
                application.MainWindow = window;
                using (var controller = new PipeController(window))
                {
                    controller.Start();
                    if (command != "--hide")
                        window.Show();
                    if (command == "--refresh")
                        window.RefreshUsage();
                    application.Run();
                }
                return 0;
            }
        }

        private static string ParseCommand(string[] args)
        {
            if (args != null)
            {
                foreach (string argument in args)
                {
                    string value = (argument ?? string.Empty).Trim().ToLowerInvariant();
                    if (value == "--show" || value == "--hide" ||
                        value == "--refresh" || value == "--quit")
                        return value;
                }
            }
            return "--show";
        }
    }
}
