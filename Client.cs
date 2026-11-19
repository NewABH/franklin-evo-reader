using System.Net.Sockets;

namespace FranklinEvo;

// TCP-клиент одного устройства. Сохраняет соединение между опросами;
// при ошибке закрывает его, чтобы повторный запрос не получил остатки старого ответа.
public sealed class Client(Options options) : IDisposable
{
    Socket? socket;
    // Последние принятые байты, в том числе неполный ответ, для --capture-dir.
    public byte[] LastFrame { get; private set; } = [];
    const int MaximumBytes = 1024 * 1024;

    public void Dispose() { socket?.Dispose(); socket = null; }

    async Task<byte[]> Receive(CancellationToken cancellation)
    {
        // Один таймаут на весь ответ, а не отдельный таймаут на каждый TCP-фрагмент.
        // Связанный токен также позволяет прервать ожидание по Ctrl+C.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.ReadTimeout));
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                // TCP не сохраняет границы сообщений: накапливаем фрагменты до ETX (0x03).
                int count = await socket!.ReceiveAsync(buffer, SocketFlags.None, deadline.Token);
                if (count == 0) throw new IOException(data.Length == 0 ? "Empty response / peer closed connection" : "Peer closed connection during response");
                int start = 0;
                // Консоль может прислать CR/LF после предыдущего кадра отдельным пакетом.
                if (data.Length == 0) while (start < count && buffer[start] is 10 or 13) start++;
                data.Write(buffer, start, count - start);
                LastFrame = data.ToArray();
                if (data.Length > MaximumBytes) throw new ProtocolException("Response size limit exceeded");
                int end = Array.IndexOf(LastFrame, (byte)3);
                if (end < 0) continue;
                // После ETX допускается только служебное заполнение, не второй ответ.
                for (int i = end + 1; i < LastFrame.Length; i++)
                    if (LastFrame[i] is not (0 or 10 or 13)) throw new ProtocolException("Unexpected bytes after ETX");
                return LastFrame[..(end + 1)];
            }
        }
        // Отмену пользователя не превращаем в сетевой таймаут: её обработает Main.
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("Response deadline exceeded"); }
    }

    public async Task<Dictionary<int, Dictionary<string, object>>> Query(Query query, CancellationToken cancellation)
    {
        // Проверка разрешённой команды выполняется до подключения и отправки байтов.
        byte[] request = Protocol.Request(query.Command);
        LastFrame = [];
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (socket is null)
                {
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    connect.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeout));
                    try { await socket.ConnectAsync(options.Host!, options.Port, connect.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new TimeoutException("Connection timeout"); }
                }
                using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                send.CancelAfter(TimeSpan.FromSeconds(options.ReadTimeout));
                try
                {
                    // SendAsync вправе отправить лишь часть буфера; дописываем остаток.
                    int offset = 0;
                    while (offset < request.Length)
                    {
                        int written = await socket.SendAsync(request.AsMemory(offset), SocketFlags.None, send.Token);
                        if (written == 0) throw new IOException("Connection closed while sending request");
                        offset += written;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new TimeoutException("Send timeout"); }
                return Protocol.Parse(await Receive(cancellation), query.Command, query.Parameters);
            }
            // Повтор не поможет, если консоль отвергла команду или не содержит нужного поля.
            catch (Exception ex) when (ex is UnsupportedCommandException or UnavailableParameterException)
            { Dispose(); throw; }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ProtocolException)
            {
                // Retries — число повторов сверх первой попытки. Повторяем только чтение.
                Dispose();
                if (attempt == options.Retries) throw;
                await Task.Delay(TimeSpan.FromSeconds(options.RetryDelay), cancellation);
            }
        }
    }
}
