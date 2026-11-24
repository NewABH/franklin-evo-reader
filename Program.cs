using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace FranklinEvo;

// Настройки одного запуска. Нулевой Tank означает все резервуары,
// Parameters == null — полный JSON, Interval == null — однократный опрос.
public sealed class Options
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public int Tank { get; set; }
    public string[]? Parameters { get; set; }
    public string Separator { get; set; } = "dot";
    public double ConnectTimeout { get; set; } = 3;
    public double ReadTimeout { get; set; } = 5;
    public int Retries { get; set; } = 2;
    public double RetryDelay { get; set; } = 1;
    public double? Interval { get; set; }
    public string? CaptureDirectory { get; set; }
    public bool Help { get; set; }
    public bool ListParameters { get; set; }

    public static Options Parse(string[] arguments)
    {
        // Приводим --ключ=значение к той же форме, что и --ключ значение.
        var args = new List<string>();
        foreach (string arg in arguments)
        {
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            { args.Add(arg[..equals]); args.Add(arg[(equals + 1)..]); }
            else args.Add(arg);
        }
        var options = new Options();
        // Читает значение следующего аргумента и одновременно сдвигает индекс разбора.
        string Value(ref int i)
        {
            if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {args[i - 1]}");
            return args[i];
        }
        static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result : throw new ArgumentException($"Invalid integer: {text}");
        // Верхняя граница укладывается в допустимый диапазон таймеров .NET.
        static double Positive(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            && double.IsFinite(result) && result > 0 && result <= 4294967
            ? result : throw new ArgumentException("Timeout/delay must be finite, positive and at most 4294967 seconds");
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--help": case "-h": options.Help = true; break;
                case "--list-parameters": options.ListParameters = true; break;
                case "--host": case "--ip": options.Host = Value(ref i); break;
                case "--port": options.Port = Integer(Value(ref i)); break;
                case "--tank":
                    options.Tank = Integer(Value(ref i));
                    if (options.Tank is < 1 or > 99) throw new ArgumentException("Tank: 1..99");
                    break;
                case "--parameter": case "--param":
                    var names = new List<string>();
                    // Список параметров продолжается до следующей опции; порядок сохраняем.
                    while (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    {
                        string name = args[++i] == "product_level" ? "level" : args[i];
                        if (!Protocol.Parameters.ContainsKey(name)) throw new ArgumentException($"Unknown parameter: {name}");
                        names.Add(name);
                    }
                    if (names.Count == 0) throw new ArgumentException("--parameter needs at least one name");
                    if (names.Distinct().Count() != names.Count) throw new ArgumentException("Duplicate parameters are not allowed");
                    options.Parameters = names.ToArray();
                    break;
                case "--decimal-separator":
                    options.Separator = Value(ref i);
                    if (options.Separator is not ("dot" or "comma")) throw new ArgumentException("Separator: dot or comma");
                    break;
                case "--connect-timeout": options.ConnectTimeout = Positive(Value(ref i)); break;
                case "--read-timeout": options.ReadTimeout = Positive(Value(ref i)); break;
                case "--retry-delay": options.RetryDelay = Positive(Value(ref i)); break;
                case "--retries": options.Retries = Integer(Value(ref i)); break;
                case "--interval": options.Interval = Positive(Value(ref i)); break;
                case "--capture-dir": options.CaptureDirectory = Value(ref i); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        // Справка доступна без адреса и порта. Для опроса проверяем настройки до сети.
        if (!options.Help && !options.ListParameters)
        {
            if (string.IsNullOrWhiteSpace(options.Host) || options.Port == 0) throw new ArgumentException("--host (or --ip) and --port are required");
            if (options.Port is < 1 or > 65535 || options.Retries is < 0 or > 10) throw new ArgumentException("Port: 1..65535; retries: 0..10");
        }
        return options;
    }
}

// Точка входа связывает CLI, TCP-клиент и форматирование результата.
public static class Program
{
    public const string Help = """
        FranklinEvoReader - read-only Franklin EVO inventory reader (C#).
        Usage: FranklinEvoReader.exe --host ADDRESS --port PORT [options]
          --host, --ip ADDRESS           Required device address
          --port PORT                    Required TCP port (normally 8001)
          --tank 1..99                   Default: all tanks
          --parameter, --param NAME ...  Default: all available fields as JSON
          --decimal-separator dot|comma  Selected-value output only; default: dot
          --connect-timeout SECONDS      Default: 3
          --read-timeout SECONDS         Default: 5 (whole response deadline)
          --retries 0..10                Default: 2 reconnection retries
          --retry-delay SECONDS          Default: 1
          --interval SECONDS             Repeat polls, reuse connection; Ctrl+C stops
          --capture-dir PATH             Save raw response .bin files
          --list-parameters              List parameter names, no connection
          --help, -h                     Show this help
        Only i214TT read commands are permitted; no fallback to other reports.
        """;

    public static async Task<int> Main(string[] args)
    {
        // Единая кодировка stdout/stderr для консоли и перенаправления в файл/канал.
        // Задаём её до первого вывода, чтобы русские сообщения Windows не зависели от OEM-кодировки.
        Console.OutputEncoding = new UTF8Encoding(false);
        // Коды выхода: 0 — успех, 1 — ошибка опроса/файлов, 2 — аргументы, 130 — Ctrl+C.
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"Argument error: {ex.Message}"); return 2; }
        if (options.Help) { Console.WriteLine(Help); return 0; }
        if (options.ListParameters)
        {
            foreach (var (name, description) in Protocol.Parameters) Console.WriteLine($"{name}: {description}");
            return 0;
        }
        // Ctrl+C отменяет сетевые ожидания и паузы; using гарантирует закрытие сокета.
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await Run(options, cancellation.Token); }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine($"Local I/O error: {ex.Message}"); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static async Task<int> Run(Options options, CancellationToken cancellation)
    {
        // Один клиент на весь запуск: в циклическом режиме используем тот же сокет.
        using var client = new Client(options);
        if (options.CaptureDirectory is not null) Directory.CreateDirectory(options.CaptureDirectory);
        var query = Protocol.CreateQuery(options.Tank, options.Parameters);
        // Запоминаем ошибки циклов, даже если последующий опрос оказался успешным.
        bool failed = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                Dictionary<int, Dictionary<string, object>> values;
                try { values = await client.Query(query, cancellation); }
                // Сохраняем принятые байты и при ошибке разбора — это помогает диагностике.
                finally
                {
                    if (options.CaptureDirectory is not null && client.LastFrame.Length > 0)
                    {
                        string filename = $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}_{query.Command}_{Guid.NewGuid():N}.bin";
                        await File.WriteAllBytesAsync(Path.Combine(options.CaptureDirectory, filename), client.LastFrame, cancellation);
                    }
                }
                string output;
                // Полный отчёт — JSON. Явный выбор — значения через ';';
                // при опросе всех резервуаров добавляем номер и имена полей в каждой строке.
                if (options.Parameters is null) output = Protocol.Json(values);
                else
                {
                    if (values.Values.Any(row => options.Parameters.Any(p => !row.ContainsKey(p))))
                        throw new UnavailableParameterException("Requested fields are missing for one or more tanks");
                    output = options.Tank == 0
                        ? string.Join(Environment.NewLine, values.OrderBy(pair => pair.Key).Select(pair => $"tank={pair.Key};" + string.Join(';', options.Parameters.Select(p => $"{p}={Protocol.Format(pair.Value[p], p, options.Separator)}"))))
                        : string.Join(';', options.Parameters.Select(p => Protocol.Format(values[options.Tank][p], p, options.Separator)));
                }
                // Печатаем результат только после успешного разбора и форматирования целиком.
                Console.WriteLine(output);
            }
            catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or TimeoutException or UnauthorizedAccessException)
            {
                failed = true;
                // stderr отделяет диагностику от stdout, который может читать другая программа.
                Console.Error.WriteLine(FormatError(ex));
            }
            if (options.Interval is null) break;
            await Task.Delay(TimeSpan.FromSeconds(options.Interval.Value), cancellation);
        }
        return failed ? 1 : 0;
    }

    // Числовой код и имя SocketError позволяют диагностировать ошибку независимо от языка ОС.
    public static string FormatError(Exception error) => error is SocketException socketError
        ? $"SocketException [{socketError.NativeErrorCode}, {socketError.SocketErrorCode}]: {socketError.Message}"
        : $"{error.GetType().Name}: {error.Message}";
}
