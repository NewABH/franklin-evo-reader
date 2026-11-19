using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FranklinEvo;

// Раздельные типы ошибок позволяют клиенту отличать повреждённый кадр
// (можно повторить запрос) от неподдерживаемой команды/отсутствующего параметра.
public class ProtocolException(string message) : Exception(message);
public sealed class UnsupportedCommandException(string message) : ProtocolException(message);
public sealed class UnavailableParameterException(string message) : ProtocolException(message);
// Parameters == null означает все поля; список имён фильтрует декодирование и вывод,
// но не меняет состав отчёта, который возвращает само устройство.
public sealed record Query(string Command, string[]? Parameters);

// Разбор машинного ASCII-отчёта i214 и представление его значений. Сетевых вызовов здесь нет.
public static class Protocol
{
    // Допустимые имена CLI и описания для --list-parameters.
    public static readonly Dictionary<string, string> Parameters = new()
    {
        ["level"] = "Product level, 2 decimal places",
        ["density"] = "Product density / 10, rounded to an integer",
        ["temperature"] = "Product temperature, 2 decimal places",
        ["volume"] = "Product volume, 2 decimal places",
        ["tc_volume"] = "Temperature-compensated volume, 2 decimal places",
        ["ullage"] = "Available tank volume, 2 decimal places",
        ["water_level"] = "Water height, 2 decimal places",
        ["water_volume"] = "Water volume, 2 decimal places, if supplied",
        ["mass"] = "Product mass, 2 decimal places, console-native units, no scaling",
        ["tc_density"] = "TC density / 10, rounded to an integer, if supplied",
        ["product_code"] = "Product code from the console",
        ["status"] = "Inventory status mask, four hexadecimal characters",
        ["delivery_in_progress"] = "Delivery flag: 1 or 0",
        ["leak_test_in_progress"] = "Leak test flag: 1 or 0",
        ["invalid_fuel_height"] = "Invalid fuel height flag: 1 or 0",
        ["unknown_status_bits"] = "Remaining status bits, four hexadecimal characters",
        ["tank"] = "Tank number from the response",
        ["device_time"] = "Console time as YYMMDDHHmm, no timezone conversion"
    };
    public static readonly HashSet<string> TwoDecimals = ["level", "temperature", "volume", "tc_volume", "ullage", "water_level", "water_volume", "mass"];
    // Для плотности действует отдельное правило: деление на 10 и округление до целого.
    public static readonly HashSet<string> Densities = ["density", "tc_density"];
    static readonly HashSet<string> Derived = ["delivery_in_progress", "leak_test_in_progress", "invalid_fuel_height", "unknown_status_bits"];
    // Позиции измерений в i214. Базовый ответ EVO содержит первые шесть полей.
    // Дополнительные позиции читаются только при наличии в кадре; при адаптации
    // к другой консоли/прошивке нужно сверить их порядок с её документацией.
    static readonly string[] Fields214 = ["volume", "mass", "density", "level", "water_level", "temperature", "tc_density", "tc_volume", "ullage", "water_volume"];
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Query CreateQuery(int tank, string[]? parameters)
    {
        // Суффикс 00 запрашивает все резервуары; 01..99 — конкретный резервуар.
        string suffix = tank.ToString("D2", Inv);
        return new("i214" + suffix, parameters);
    }

    public static byte[] Request(string command)
    {
        if (command.Length != 6 || command[..4] != "i214" || !Digits(command[4..]))
            throw new ArgumentException("Only i214TT for tank 00..99 is allowed");
        // Запрос: SOH (0x01) + шесть ASCII-символов, без завершающего ETX.
        return Encoding.ASCII.GetBytes("\x01" + command);
    }
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        // Дополнение суммы байтов до нуля по модулю 65536.
        // В сумму ответа входят SOH и оба символа &, но не сама сумма и ETX.
        int sum = 0;
        foreach (byte b in data) sum += b;
        return unchecked((ushort)-sum);
    }
    static bool Digits(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9');
    static bool Hex(string text) => text.Length > 0 && text.All(Uri.IsHexDigit);

    public static Dictionary<int, Dictionary<string, object>> Parse(byte[] frame, string command, string[]? parameters)
    {
        // Кадр: SOH + команда(6) + YYMMDDHHmm(10) + записи резервуаров
        //       + "&&" + контрольная сумма(4 hex-символа) + ETX.
        // Все смещения ниже считаются в ASCII-символах, не в двоичных float.
        Request(command);
        if (frame.Length < 2 || frame[0] != 1 || frame[^1] != 3) throw new ProtocolException("Missing SOH/ETX");
        // Короткий ответ SOH + "9999FF1B" + ETX означает отказ команды.
        if (frame.AsSpan().SequenceEqual(new byte[] { 1, 57, 57, 57, 57, 70, 70, 49, 66, 3 }))
            throw new UnsupportedCommandException("Device rejected command (9999)");
        if (frame.Length < 24 || frame[^7] != '&' || frame[^6] != '&') throw new ProtocolException("Invalid checksum trailer");
        string check = Encoding.ASCII.GetString(frame, frame.Length - 5, 4);
        if (!Hex(check)) throw new ProtocolException("Invalid checksum trailer");
        if (ushort.Parse(check, NumberStyles.HexNumber, Inv) != Checksum(frame.AsSpan(0, frame.Length - 5)))
            throw new ProtocolException("Checksum mismatch");
        if (frame.AsSpan(1, frame.Length - 8).ContainsAnyInRange((byte)128, (byte)255)) throw new ProtocolException("Non-ASCII response");
        string text = Encoding.ASCII.GetString(frame, 1, frame.Length - 8);
        if (!text.StartsWith(command, StringComparison.Ordinal)) throw new ProtocolException("Response command does not match request");
        string timestamp = text.Substring(6, 10);
        if (!Digits(timestamp)) throw new ProtocolException("Invalid timestamp field");
        // Внешний ключ — номер резервуара; внутренний — имя поля.
        // Значения: int для номера/флагов, string для кодов/времени, double для измерений.
        var result = new Dictionary<int, Dictionary<string, object>>();
        string[] fields = Fields214;
        int requestedTank = int.Parse(command[4..], Inv);
        int pos = 16;
        while (pos < text.Length)
        {
            if (text.Length - pos < 9) throw new ProtocolException("Missing or invalid tank record");
            // Заголовок записи: номер(2 цифры), продукт(1 символ), статус(4 hex),
            // число измерений NN(2 hex). Каждое измерение занимает 8 hex-символов.
            string header = text.Substring(pos, 9);
            if (!Digits(header[..2]) || header[2] < ' ' || header[2] > '~' || !Hex(header[3..]))
                throw new ProtocolException("Invalid tank header");
            int tank = int.Parse(header[..2], Inv);
            int status = int.Parse(header.Substring(3, 4), NumberStyles.HexNumber, Inv);
            int count = int.Parse(header[7..], NumberStyles.HexNumber, Inv);
            int end = pos + 9 + count * 8;
            if (tank == 0 || result.ContainsKey(tank) || (requestedTank != 0 && tank != requestedTank))
                throw new ProtocolException("Invalid/duplicate tank or response contains a different tank");
            if (count < 6 || end > text.Length || !Hex(text.Substring(pos + 9, count * 8)))
                throw new ProtocolException("Invalid field count or truncated inventory");
            // Биты 0..2 расшифровываются для явного выбора пользователем.
            // Неизвестные биты сохраняются, чтобы не терять информацию консоли.
            var metadata = new Dictionary<string, object>
            {
                ["tank"] = tank, ["product_code"] = header[2].ToString(), ["device_time"] = timestamp,
                ["status"] = status.ToString("X4", Inv), ["delivery_in_progress"] = (status & 1) != 0 ? 1 : 0,
                ["leak_test_in_progress"] = (status & 2) != 0 ? 1 : 0,
                ["invalid_fuel_height"] = (status & 4) != 0 ? 1 : 0,
                ["unknown_status_bits"] = (status & ~7).ToString("X4", Inv)
            };
            // Число полей берём из NN; неизвестным позициям даём нейтральные имена.
            var names = Enumerable.Range(0, count).Select(i => i < fields.Length ? fields[i] : $"field_{i + 1}").ToArray();
            var row = new Dictionary<string, object>();
            foreach (string name in parameters ?? metadata.Keys.Concat(names).ToArray())
            {
                if (metadata.TryGetValue(name, out object? value)) { row[name] = value; continue; }
                int index = Array.IndexOf(names, name);
                if (index < 0) throw new UnavailableParameterException($"Parameter {name} is unavailable in {command} ({count} fields)");
                uint bits = uint.Parse(text.AsSpan(pos + 9 + index * 8, 8), NumberStyles.HexNumber, Inv);
                // Восемь hex-символов задают биты IEEE 754 float32 (старшие первыми).
                // Это переинтерпретация битов, не числовое преобразование uint во float.
                // Расширение до double сохраняет точное значение float32, включая плотность.
                double number = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                if (!double.IsFinite(number)) throw new ProtocolException($"Non-finite value: {name}");
                row[name] = number;
            }
            result.Add(tank, row);
            pos = end;
        }
        if (requestedTank != 0 && result.Count != 1) throw new ProtocolException("Missing or extra tank record");
        return result;
    }

    public static string Format(object value, string name, string separator = "dot")
    {
        // Формат R сохраняет декодированное значение при обратном чтении числа.
        // InvariantCulture исключает зависимость результата от локали Windows.
        string text = value is double d ? d.ToString("R", Inv) : Convert.ToString(value, Inv)!;
        if (TwoDecimals.Contains(name) || Densities.Contains(name))
        {
            // Десятичное округление половин от нуля. BigInteger позволяет работать
            // со всем диапазоном float32 без переполнения decimal и ошибок двоичного округления.
            bool negative = text.StartsWith('-');
            string abs = text.TrimStart('-', '+');
            string[] parts = abs.Split(['e', 'E']);
            int exponent = parts.Length > 1 ? int.Parse(parts[1], Inv) : 0;
            int dot = parts[0].IndexOf('.');
            int fractional = dot < 0 ? 0 : parts[0].Length - dot - 1;
            BigInteger digits = BigInteger.Parse(parts[0].Replace(".", ""), Inv);
            int precision = Densities.Contains(name) ? 0 : 2;
            // Для плотности сдвиг на один разряд выполняет деление на 10
            // до округления; для остальных величин целое число означает сотые доли.
            int shift = exponent - fractional + precision - (Densities.Contains(name) ? 1 : 0);
            if (shift >= 0) digits *= BigInteger.Pow(10, shift);
            else
            {
                BigInteger divisor = BigInteger.Pow(10, -shift);
                digits = BigInteger.DivRem(digits, divisor, out BigInteger remainder);
                if (remainder * 2 >= divisor) digits++;
            }
            string formatted = digits.ToString(Inv).PadLeft(precision + 1, '0');
            // У округлённого нуля убираем минус, например -0.001 -> 0.00.
            text = (negative && digits != 0 ? "-" : "") +
                (precision == 0 ? formatted : formatted.Insert(formatted.Length - precision, "."));
        }
        if (separator == "comma" && (TwoDecimals.Contains(name) || Densities.Contains(name) || name.StartsWith("field_", StringComparison.Ordinal)))
            text = text.Replace('.', ',');
        return text;
    }

    public static string Json(Dictionary<int, Dictionary<string, object>> tanks)
    {
        // Явная запись JSON не требует рефлексии и совместима с Native AOT.
        // Выводим только данные консоли; вычисленные флаги статуса в полный JSON не входят.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var (tank, row) in tanks.OrderBy(pair => pair.Key))
            {
                writer.WriteStartObject();
                writer.WriteNumber("tank", tank);
                foreach (var (name, value) in row)
                {
                    if (name == "tank" || Derived.Contains(name)) continue;
                    writer.WritePropertyName(name == "level" ? "product_level" : name);
                    if (name == "device_time")
                    {
                        // Граница двузначного года: 00..68 -> 2000..2068; 69..99 -> 1969..1999.
                        string time = (string)value;
                        int yy = int.Parse(time[..2], Inv);
                        string year = ((yy <= 68 ? 2000 : 1900) + yy).ToString(Inv);
                        if (!DateTime.TryParseExact(year + time[2..], "yyyyMMddHHmm", Inv, DateTimeStyles.None, out var date))
                            throw new ProtocolException("Invalid console date/time");
                        writer.WriteStringValue(date.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv));
                    }
                    // Плотность и округляемые измерения пишем числами, без кавычек.
                    // Неизвестные дополнительные измерения сохраняем без преобразований.
                    else if (TwoDecimals.Contains(name) || Densities.Contains(name)) writer.WriteRawValue(Format(value, name));
                    else if (value is double number) writer.WriteNumberValue(number);
                    else if (value is int integer) writer.WriteNumberValue(integer);
                    else writer.WriteStringValue((string)value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
