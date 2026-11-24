using FranklinEvo;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

static class Tests
{
    static int passed;
    static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    static void Equal<T>(T actual, T expected) => Assert(Equals(actual, expected), $"Expected {expected}; received {actual}");
    static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    static byte[] Frame(string payload)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("\x01" + payload + "&&");
        return bytes.Concat(Encoding.ASCII.GetBytes(Protocol.Checksum(bytes).ToString("X4") + "\x03")).ToArray();
    }
    static string Record(int tank = 4, float[]? values = null, string status = "0000")
    {
        values ??= [18722.11f, 34765.24609375f, 739.85f, 922.125f, 0, 15.285f];
        return $"{tank:D2}4{status}{values.Length:X2}" + string.Concat(values.Select(v => BitConverter.SingleToInt32Bits(v).ToString("X8")));
    }
    static byte[] Packed(string command = "i21404", int tank = 4, float[]? values = null, string status = "0000")
        => Frame(command + "2611161200" + Record(tank, values, status));
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    static async Task<string> ReadRequest(NetworkStream stream, CancellationToken token)
    {
        byte[] bytes = new byte[7];
        await stream.ReadExactlyAsync(bytes, token);
        Equal(bytes[0], (byte)1);
        return Encoding.ASCII.GetString(bytes, 1, 6);
    }
    static Options Connection(int port) => new() { Host = "127.0.0.1", Port = port, Tank = 4, Retries = 0, ReadTimeout = 0.2, ConnectTimeout = 1, RetryDelay = 0.001 };

    static async Task<int> Main(string[] args)
    {
        try
        {
            Test("default CLI and aliases", () =>
            {
                var o = Options.Parse(["--ip=localhost", "--port", "8001"]);
                Equal(o.Tank, 0); Assert(o.Parameters is null);
                Equal(Options.Parse(["--host", "x", "--port", "8001", "--param", "product_level"]).Parameters![0], "level");
                Assert(Options.Parse(["--list-parameters"]).ListParameters);
                Assert(Options.Parse(["--help"]).Help);
            });
            Test("socket error includes stable codes", () =>
            {
                var error = new SocketException(10061);
                Equal(FranklinEvo.Program.FormatError(error), $"SocketException [10061, ConnectionRefused]: {error.Message}");
                Equal(FranklinEvo.Program.FormatError(new IOException("Ошибка чтения")), "IOException: Ошибка чтения");
            });
            Test("invalid CLI", () =>
            {
                foreach (string[] input in new string[][] { [], ["--host", "x"], ["--port", "8001"],
                    ["--host", "x", "--port", "0"], ["--host", "x", "--port", "65536"],
                    ["--tank", "0"], ["--tank", "100"], ["--param", "level", "level"],
                    ["--param", "product_level", "level"], ["--param", "bad"], ["--param"],
                    ["--interval", "NaN"], ["--interval", "Infinity"], ["--read-timeout", "0"],
                    ["--commands", "S531001"], ["--host", "x", "--port", "8001", "--retries", "11"] })
                    Throws<ArgumentException>(() => Options.Parse(input));
            });
            Test("read-only command allowlist", () =>
            {
                Equal(Encoding.ASCII.GetString(Protocol.Request("i21404")), "\x01i21404");
                foreach (string cmd in new[] { "i20100", "i20104", "S21404", "I20104", "i20504", "i21404\x03S531001", "i201AA" })
                    Throws<ArgumentException>(() => Protocol.Request(cmd));
            });
            Test("query selection", () =>
            {
                Equal(Protocol.CreateQuery(0, null).Command, "i21400");
                Equal(Protocol.CreateQuery(4, ["level", "temperature"]).Command, "i21404");
                var query = Protocol.CreateQuery(4, ["water_volume", "density", "tc_volume", "mass"]);
                Equal(string.Join(',', query.Parameters!), "water_volume,density,tc_volume,mass");
                foreach (string parameter in Protocol.Parameters.Keys)
                {
                    Equal(Protocol.CreateQuery(0, [parameter]).Command, "i21400");
                }
            });
            Test("decimal rounding and negative zero", () =>
            {
                foreach (string name in Protocol.TwoDecimals)
                    foreach (var (value, expected) in new[] { (15.285, "15.29"), (-15.285, "-15.29"), (15.284, "15.28"), (0.0, "0.00"), (-0.001, "0.00"), (34765.24609375, "34765.25") })
                    {
                        Equal(Protocol.Format(value, name), expected);
                        Equal(Protocol.Format(value, name, "comma"), expected.Replace('.', ','));
                    }
                Equal(Protocol.Format((double)float.MaxValue, "volume"), "340282346638528860000000000000000000000.00");
                Equal(Protocol.Format((double)float.Epsilon, "volume"), "0.00");
            });
            Test("density rounded to integer without scaling", () =>
            {
                foreach (var (value, expected) in new[] { (739.85, "740"), (739.5, "740"), (739.49, "739"), (-739.5, "-740"), (735.0, "735"), (-0.1, "0"), (1000.0, "1000"), (0.0, "0") })
                    foreach (string name in Protocol.Densities) Equal(Protocol.Format(value, name), expected);
            });
            Test("decode float32 and selected fields", () =>
            {
                var row = Protocol.Parse(Packed(), "i21404", ["density", "mass", "level"])[4];
                Equal(row.Count, 3); Equal((double)row["level"], 922.125);
                Equal(Protocol.Format(row["mass"], "mass"), "34765.25");
                Equal(Protocol.Format(row["density"], "density", "comma"), "740");
            });
            Test("all tanks and duplicate protection", () =>
            {
                var rows = Protocol.Parse(Frame("i214002611161200" + string.Concat(Enumerable.Range(1, 4).Select(t => Record(t)))), "i21400", null);
                Equal(rows.Count, 4);
                for (int i = 1; i <= 4; i++) Equal((int)rows[i]["tank"], i);
                Throws<ProtocolException>(() => Protocol.Parse(Frame("i214002611161200" + Record(1) + Record(1)), "i21400", null));
                Throws<ProtocolException>(() => Protocol.Parse(Packed("i21400", 0), "i21400", null));
                Equal(Protocol.Parse(Frame("i214002611161200"), "i21400", null).Count, 0);
            });
            Test("optional fields and unknown fields", () =>
            {
                var row = Protocol.Parse(Packed(), "i21404", null)[4];
                Assert(!row.ContainsKey("tc_density"));
                Throws<UnavailableParameterException>(() => Protocol.Parse(Packed(), "i21404", ["tc_density"]));
                row = Protocol.Parse(Packed(values: [1, 2, 740, 4, 5, 6, 743, 8, 9, 10, 11]), "i21404", null)[4];
                Equal((double)row["field_11"], 11.0); Equal((double)row["tc_volume"], 8.0);
            });
            Test("status flags", () =>
            {
                var row = Protocol.Parse(Packed(status: "8005"), "i21404", null)[4];
                Equal((string)row["status"], "8005"); Equal((string)row["unknown_status_bits"], "8000");
                Equal((int)row["delivery_in_progress"], 1); Equal((int)row["leak_test_in_progress"], 0);
                Equal((int)row["invalid_fuel_height"], 1);
            });
            Test("checksum, rejection and malformed frames", () =>
            {
                byte[] bad = Packed(); bad[^5] = bad[^5] == '0' ? (byte)'1' : (byte)'0';
                Throws<ProtocolException>(() => Protocol.Parse(bad, "i21404", null));
                Throws<UnsupportedCommandException>(() => Protocol.Parse(Encoding.ASCII.GetBytes("\x01" + "9999FF1B\x03"), "i21404", null));
                foreach (byte[] frame in new[] { Packed(tank: 3), Packed("i21403"), Packed()[..^8], Encoding.ASCII.GetBytes("garbage"), Packed(values: [1, 2]), Packed(values: [1, 2, float.NaN, 4, 5, 6]), Frame("i214042611161200" + Record() + Record(3)), Frame("i21404XXXXXXXXXX" + Record()) })
                    Throws<ProtocolException>(() => Protocol.Parse(frame, "i21404", null));
            });
            Test("JSON device fields and numeric formatting", () =>
            {
                var tanks = Protocol.Parse(Packed(values: [1000, 34765.24609375f, 739.85f, 100, 0, 15.285f]), "i21404", null);
                using var json = JsonDocument.Parse(Protocol.Json(tanks));
                var row = json.RootElement[0]; Equal(row.GetProperty("volume").GetDouble(), 1000.0);
                Equal(row.GetProperty("mass").GetDouble(), 34765.25); Equal(row.GetProperty("density").GetDouble(), 740.0);
                Equal(row.GetProperty("device_time").GetString(), "2026-11-16T12:00:00");
                Equal(row.EnumerateObject().Count(), 10);
                foreach (string name in new[] { "commands", "ok", "checksum_verified", "attempts", "reports", "units", "delivery_in_progress", "measurements" }) Assert(!row.TryGetProperty(name, out _));
                foreach (string name in new[] { "tc_volume", "ullage", "water_volume" }) Assert(!row.TryGetProperty(name, out _));
            });
            await Test("fragmentation, reconnect and connection reuse", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var requests = new List<string>();
                var server = Task.Run(async () =>
                {
                    using (var broken = await listener.AcceptTcpClientAsync(timeout.Token))
                    {
                        requests.Add(await ReadRequest(broken.GetStream(), timeout.Token));
                        await broken.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\x01i214"), timeout.Token);
                    }
                    using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                    for (int i = 0; i < 2; i++)
                    {
                        requests.Add(await ReadRequest(connection.GetStream(), timeout.Token));
                        foreach (byte b in Packed()) await connection.GetStream().WriteAsync(new byte[] { b }, timeout.Token);
                    }
                });
                var options = Connection(((IPEndPoint)listener.LocalEndpoint).Port); options.Retries = 1; options.ReadTimeout = 2;
                using var client = new Client(options);
                for (int i = 0; i < 2; i++) Assert((await client.Query(new("i21404", ["density"]), timeout.Token))[4].ContainsKey("density"));
                await server; Equal(string.Join(',', requests), "i21404,i21404,i21404");
            });
            foreach (string scenario in new[] { "empty", "timeout", "bad checksum", "rejected", "unavailable", "oversized" })
                await Test("TCP " + scenario, async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                    var server = Task.Run(async () =>
                    {
                        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                        await ReadRequest(connection.GetStream(), timeout.Token);
                        if (scenario == "timeout") await Task.Delay(500, timeout.Token);
                        else if (scenario == "empty") return;
                        else
                        {
                            byte[] frame = scenario switch
                            {
                                "rejected" => Encoding.ASCII.GetBytes("\x01" + "9999FF1B\x03"),
                                "oversized" => Enumerable.Repeat((byte)'x', 1024 * 1024 + 1).ToArray(),
                                _ => Packed()
                            };
                            if (scenario == "bad checksum") frame[^5] = (byte)'Z';
                            await connection.GetStream().WriteAsync(frame, timeout.Token);
                        }
                    });
                    var options = Connection(((IPEndPoint)listener.LocalEndpoint).Port);
                    if (scenario is "rejected" or "unavailable") options.Retries = 2;
                    using var client = new Client(options);
                    Func<Task> query = async () => { await client.Query(new("i21404", [scenario == "unavailable" ? "tc_density" : "density"]), timeout.Token); };
                    switch (scenario)
                    {
                        case "empty": await ThrowsAsync<IOException>(query); break;
                        case "timeout": await ThrowsAsync<TimeoutException>(query); break;
                        case "rejected": await ThrowsAsync<UnsupportedCommandException>(query); break;
                        case "unavailable": await ThrowsAsync<UnavailableParameterException>(query); break;
                        default: await ThrowsAsync<ProtocolException>(query); break;
                    }
                    await server;
                });
            await Test("connection refused", async () =>
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var options = Connection(port); options.ConnectTimeout = 5;
                using var client = new Client(options);
                await ThrowsAsync<SocketException>(async () => { await client.Query(new("i21404", null), CancellationToken.None); });
            });
            await Test("cancellation", async () =>
            {
                using var token = new CancellationTokenSource(); token.Cancel();
                using var client = new Client(Connection(8001));
                await ThrowsAsync<OperationCanceledException>(async () => { await client.Query(new("i21404", null), token.Token); });
            });
            if (args.Length > 0 && args[0] != "-")
                Test("saved real device frames", () =>
                {
                    int count = 0;
                    foreach (string file in Directory.EnumerateFiles(args[0], "*.bin", SearchOption.AllDirectories))
                    {
                        byte[] raw = File.ReadAllBytes(file);
                        if (raw.Length < 7 || raw[0] != 1) continue;
                        string command = Encoding.ASCII.GetString(raw, 1, 6);
                        if (!command.StartsWith("i214")) continue;
                        int end = Array.IndexOf(raw, (byte)3);
                        if (end < 0) continue;
                        using var json = JsonDocument.Parse(Protocol.Json(Protocol.Parse(raw[..(end + 1)], command, null)));
                        count++;
                    }
                    Assert(count > 0, "No captured frames found"); Console.WriteLine($"Decoded {count} captured frames");
                });
            if (args.Length > 1)
            {
                string executable = Path.GetFullPath(args[1]);
                foreach (string scenario in new[] { "all", "single", "mass", "mixed", "level", "atomic failure", "missing field", "no fallback", "invalid arguments", "utf8 error" })
                    await Test("EXE " + scenario, async () =>
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        var requests = new List<string>();
                        string[] selection = scenario switch
                        {
                            "single" => ["--tank", "4", "--param", "density", "--decimal-separator", "comma"],
                            "mass" => ["--tank", "4", "--param", "mass", "--decimal-separator", "comma"],
                            "mixed" => ["--tank", "4", "--param", "level", "density", "mass", "--decimal-separator", "comma"],
                            "level" => ["--tank", "4", "--param", "level"],
                            "no fallback" => ["--tank", "4", "--param", "tc_volume"],
                            "missing field" => ["--tank", "4", "--param", "tc_density"],
                            "invalid arguments" => ["--tank", "0"],
                            "utf8 error" => ["--неизвестная-опция"],
                            _ => []
                        };
                        int queries = 1;
                        Task server = scenario is "invalid arguments" or "utf8 error" ? Task.CompletedTask : Task.Run(async () =>
                        {
                            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                            for (int q = 0; q < queries; q++)
                            {
                                string command = await ReadRequest(connection.GetStream(), deadline.Token);
                                requests.Add(command);
                                Assert(command.StartsWith("i214"), "Unexpected request: " + command);
                                float[] values = [1000, 34765.24609375f, 739.85f, 100, 0, 15.285f];
                                string records = command.EndsWith("00") ? string.Concat(Enumerable.Range(1, 4).Select(t => Record(t, values))) : Record(4, values);
                                byte[] frame = Frame(command + "2611161200" + records);
                                if (scenario == "atomic failure") frame[^5] = (byte)'Z';
                                await connection.GetStream().WriteAsync(frame, deadline.Token);
                            }
                        });
                        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
                        foreach (string arg in new[] { "--host", "127.0.0.1", "--port", port.ToString(), "--retries", "0" }.Concat(selection)) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!;
                        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                        try { await process.WaitForExitAsync(deadline.Token); await server; }
                        finally { if (!process.HasExited) process.Kill(true); }
                        string output = (await stdout).Trim(); string error = await stderr;
                        if (scenario is "atomic failure" or "missing field" or "no fallback" or "invalid arguments" or "utf8 error")
                        {
                            Equal(process.ExitCode, scenario is "invalid arguments" or "utf8 error" ? 2 : 1);
                            Equal(output, ""); Assert(error.Length > 0);
                            if (scenario is "invalid arguments" or "utf8 error") Assert(!listener.Pending());
                            if (scenario == "utf8 error") Equal(error.TrimEnd(), "Argument error: Unknown option: --неизвестная-опция");
                        }
                        else
                        {
                            Equal(process.ExitCode, 0); Equal(error, "");
                            switch (scenario)
                            {
                                case "all":
                                    using (var json = JsonDocument.Parse(output))
                                    {
                                        Equal(json.RootElement.GetArrayLength(), 4);
                                        Equal(json.RootElement[3].GetProperty("density").GetDouble(), 740.0);
                                        Equal(json.RootElement[3].GetProperty("mass").GetDouble(), 34765.25);
                                        Equal(json.RootElement[3].EnumerateObject().Count(), 10);
                                    }
                                    Equal(string.Join(',', requests), "i21400"); break;
                                case "single": Equal(output, "740"); Equal(requests[0], "i21404"); break;
                                case "mass": Equal(output, "34765,25"); break;
                                case "mixed": Equal(output, "100,00;740;34765,25"); Equal(string.Join(',', requests), "i21404"); break;
                                case "level": Equal(output, "100.00"); Equal(requests[0], "i21404"); break;
                            }
                        }
                    });
            }
            Console.WriteLine($"All {passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
