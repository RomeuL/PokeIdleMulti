using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace PokeIdleMulti;

/// <summary>
/// O diagnóstico do jogo que não carrega (num PC que não é o seu, onde não dá para olhar): um log por conta em
/// diagnostico\contaN.log, na pasta do deck, com o caminho da página até o jogo abrir: a navegação, os pedidos que
/// falharam ou voltaram com erro, o socket do jogo (abriu, conectou, fechou), os erros de JavaScript e do console, as
/// etapas da tela de abertura e os processos do navegador que caíram; quando o vigia recarrega, um print e o retrato da
/// página. Depois que o jogo abre, só as falhas. Nada de senha, token, cookie ou valor do localStorage (só os nomes das
/// chaves), e os endereços vão sem o "?…".
/// </summary>
internal sealed class PaneDiag
{
    /// <summary>A mensagem do script da página (os erros, o console.error) chega pelo postMessage com este começo.</summary>
    public const string MessagePrefix = "deckdiag:";

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxShots = 12;

    // Depois que o jogo abre, mais um minuto de tudo (o socket e o que ele pede logo ao entrar); depois, só as falhas.
    private static readonly TimeSpan AfterOpen = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> Important = new(StringComparer.OrdinalIgnoreCase)
        { "Document", "Script", "Stylesheet", "Fetch", "XHR", "Manifest", "EventSource" };

    private readonly string _folder;
    private readonly string _profile;
    private readonly Func<string> _name;
    private readonly object _lock = new();
    private readonly StringBuilder _pending = new();
    private readonly System.Threading.Timer _flush;
    private readonly Dictionary<string, int> _seen = new();
    private readonly Dictionary<string, Request> _requests = new();
    private readonly Dictionary<string, (string Url, DateTime At)> _sockets = new();
    private CoreWebView2? _attached;
    private DateTime _pageAt = DateTime.Now;
    private DateTime? _loadedAt;
    private DateTime? _openAt;
    private string? _state;
    private int _omitted;

    private sealed class Request
    {
        public required string Url { get; init; }
        public required string Type { get; init; }
        public DateTime At { get; } = DateTime.Now;
        public int Status { get; set; }
        public string? Source { get; set; }
        public bool Log { get; set; }
    }

    public PaneDiag(string folder, string profile, Func<string> name)
    {
        _folder = folder;
        _profile = profile;
        _name = name;
        _flush = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
    }

    /// <summary>O arquivo do log desta conta.</summary>
    public string LogPath => Path.Combine(_folder, _profile + ".log");

    /// <summary>A tela de abertura ainda não saiu (ou saiu faz pouco): anota tudo, não só as falhas.</summary>
    private bool Loading => _openAt is not DateTime open || DateTime.Now - open < AfterOpen;

    private double Since(DateTime at) => Math.Round((DateTime.Now - at).TotalSeconds, 1);

    public void Write(string text)
    {
        lock (_lock)
            _pending.Append(DateTime.Now.ToString("dd/MM HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ")
                .Append(text).Append(Environment.NewLine);
    }

    /// <summary>A mesma mensagem só <paramref name="max"/> vezes por página (o resto vira um "omitidas" no fim).</summary>
    private bool Once(string key, int max = 3)
    {
        int count = _seen.TryGetValue(key, out int c) ? c + 1 : 1;
        _seen[key] = count;
        if (count <= max)
            return true;
        _omitted++;
        return false;
    }

    public void Flush()
    {
        string text;
        lock (_lock)
        {
            if (_pending.Length == 0)
                return;
            text = _pending.ToString();
            _pending.Clear();
            try
            {
                Directory.CreateDirectory(_folder);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    // Fica a metade mais nova.
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(LogPath, text);
            }
            catch (IOException)
            {
                // Fica para a próxima.
                _pending.Insert(0, text);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ---------- O que a conta conta ----------

    public void Navigation(string uri, bool redirected)
    {
        if (redirected)
        {
            Write($"redirecionou para {Diag.Bare(uri)}");
            return;
        }
        if (_omitted > 0)
            Write($"({_omitted} mensagens repetidas omitidas na página anterior)");
        _omitted = 0;
        _seen.Clear();
        _pageAt = DateTime.Now;
        _loadedAt = _openAt = null;
        _state = null;
        Write($"===== {_name()}: abrindo {Diag.Bare(uri)}");
    }

    public void Completed(bool ok, CoreWebView2WebErrorStatus error, int http)
    {
        if (ok)
        {
            _loadedAt = DateTime.Now;
            Write($"página carregou em {Since(_pageAt)} s (HTTP {http})");
        }
        else
            Write($"a página NÃO carregou em {Since(_pageAt)} s: {error} (HTTP {http})");
    }

    /// <summary>Onde o jogo está (o que o vigia lê a cada 5 s): só quando muda.</summary>
    public void State(string state)
    {
        if (state == _state)
            return;
        _state = state;
        Write($"tela: {state} ({Since(_loadedAt ?? _pageAt)} s depois de carregar)");
        if (!state.StartsWith("carga") && _openAt is null)
            _openAt = DateTime.Now;
    }

    public void Page(string message) => Write("página: " + message[MessagePrefix.Length..]);

    public void ProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        string frames = "";
        try
        {
            if (e.FrameInfosForFailedProcess is { Count: > 0 } infos)
                frames = " frames=" + string.Join(", ", infos.Select(f => Diag.Bare(f.Source)));
        }
        catch
        {
        }
        Write($"PROCESSO CAIU: {e.ProcessFailedKind} motivo={e.Reason} código={e.ExitCode} {e.ProcessDescription}{frames}");
    }

    // ---------- O DevTools: a rede e o console do navegador ----------

    public async Task AttachAsync(CoreWebView2 core)
    {
        if (ReferenceEquals(_attached, core))
            return;
        _attached = core;
        void On(string name, Action<JsonElement> handle)
        {
            core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                    handle(doc.RootElement);
                }
                catch
                {
                    // Um evento estranho não derruba o deck.
                }
            };
        }
        On("Network.requestWillBeSent", OnRequest);
        On("Network.responseReceived", OnResponse);
        On("Network.loadingFinished", OnFinished);
        On("Network.loadingFailed", OnFailed);
        On("Network.webSocketCreated", p =>
        {
            string url = Diag.Bare(Str(p, "url"));
            _sockets[Str(p, "requestId")] = (url, DateTime.Now);
            if (Once("ws " + url, 30))
                Write($"socket abrindo: {url}");
        });
        On("Network.webSocketHandshakeResponseReceived", p =>
        {
            var response = p.GetProperty("response");
            int status = Int(response, "status");
            string took = _sockets.TryGetValue(Str(p, "requestId"), out var s) ? $" em {Since(s.At)} s" : "";
            if (Once("ws handshake " + status, 30))
                Write($"socket {(status == 101 ? "conectou" : "RECUSADO")}: HTTP {status} {Str(response, "statusText")}{took}");
        });
        On("Network.webSocketClosed", p =>
        {
            string id = Str(p, "requestId");
            string what = _sockets.Remove(id, out var s) ? $"{s.Url}, aberto {Since(s.At)} s" : "?";
            if (Once("ws closed " + what.Split(',')[0], 30))
                Write($"socket FECHOU ({what})");
        });
        On("Network.webSocketFrameError", p =>
        {
            string message = Diag.Cut(Str(p, "errorMessage"), 300);
            if (Once("ws error " + message, 10))
                Write($"socket ERRO: {message}");
        });
        On("Log.entryAdded", p =>
        {
            var entry = p.GetProperty("entry");
            string level = Str(entry, "level"), source = Str(entry, "source");
            // A rede já vem pelos pedidos; as "violações" e recomendações são barulho.
            if (level is not ("error" or "warning") || source is "network" or "violation" or "recommendation" or "deprecation")
                return;
            string text = Diag.Cut(Str(entry, "text"), 300);
            if (Once("log " + text))
                Write($"navegador {level} ({source}): {text} {Diag.Bare(Str(entry, "url"))}".TrimEnd());
        });
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable",
                "{\"maxTotalBufferSize\":1048576,\"maxResourceBufferSize\":262144,\"maxPostDataSize\":0}");
            await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        }
        catch (Exception ex)
        {
            Write("não consegui ligar o DevTools do diagnóstico: " + ex.Message);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Header(JsonElement response, string name)
    {
        if (!response.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var h in headers.EnumerateObject())
            if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? "" : "";
        return "";
    }

    private void OnRequest(JsonElement p)
    {
        var request = p.GetProperty("request");
        string url = Str(request, "url");
        if (url.StartsWith("data:") || url.StartsWith("blob:"))
            return;
        string type = Str(p, "type");
        if (p.TryGetProperty("redirectResponse", out var redirect) && type == "Document")
            Write($"documento redirecionado: HTTP {Int(redirect, "status")} → {Diag.Bare(url)}");
        if (_requests.Count > 4000)
            _requests.Clear();
        _requests[Str(p, "requestId")] = new Request { Url = Diag.Bare(url), Type = type };
    }

    private void OnResponse(JsonElement p)
    {
        var response = p.GetProperty("response");
        string type = Str(p, "type");
        int status = Int(response, "status");
        string source = Bool(response, "fromServiceWorker") ? "service worker" : Bool(response, "fromDiskCache") ? "cache de disco"
            : Bool(response, "fromPrefetchCache") ? "prefetch" : "";
        string url = Diag.Bare(Str(response, "url"));
        if (_requests.TryGetValue(Str(p, "requestId"), out var r))
        {
            r.Status = status;
            r.Source = source;
        }
        if (type == "Document" && (Loading || status >= 400))
        {
            var parts = new List<string>
            {
                $"documento HTTP {status} {Str(response, "protocol")} de {Str(response, "remoteIPAddress")}:{Int(response, "remotePort")}",
            };
            if (source.Length > 0)
                parts.Add("(" + source + ")");
            foreach (var h in new[] { "server", "cf-ray", "cf-cache-status", "cf-mitigated", "content-type" })
                if (Header(response, h) is { Length: > 0 } value)
                    parts.Add($"{h}={value}");
            if (DateTimeOffset.TryParse(Header(response, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                parts.Add("relógio do PC " + Diag.Skew(DateTimeOffset.UtcNow - date));
            Write(string.Join(" ", parts) + " " + url);
            return;
        }
        if (status >= 400)
        {
            if (Once($"http {status} {url}"))
                Write($"{type} HTTP {status}{(source.Length > 0 ? " (" + source + ")" : "")} {url}");
            return;
        }
        if (r is not null && Loading && Important.Contains(type))
            r.Log = true;
    }

    private void OnFinished(JsonElement p)
    {
        if (!_requests.Remove(Str(p, "requestId"), out var r) || !r.Log || !Loading)
            return;
        double kb = p.TryGetProperty("encodedDataLength", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetDouble() / 1024 : 0;
        if (Once("ok " + r.Url))
            Write($"{r.Type} {r.Status} {kb:0} KB em {(DateTime.Now - r.At).TotalMilliseconds:0} ms" +
                  $"{(string.IsNullOrEmpty(r.Source) ? "" : " (" + r.Source + ")")} {r.Url}");
    }

    private void OnFailed(JsonElement p)
    {
        bool canceled = Bool(p, "canceled");
        _requests.Remove(Str(p, "requestId"), out var r);
        // Depois que o jogo abriu, o cancelado é a página trocando de tela.
        if (canceled && !Loading)
            return;
        string error = Str(p, "errorText");
        string blocked = Str(p, "blockedReason");
        string cors = p.TryGetProperty("corsErrorStatus", out var c) ? Str(c, "corsError") : "";
        string url = r?.Url ?? "?";
        if (!Once($"fail {error} {url}"))
            return;
        Write($"FALHOU {Str(p, "type")} {error}{(canceled ? " (cancelado)" : "")}" +
              $"{(blocked.Length > 0 ? " bloqueado=" + blocked : "")}{(cors.Length > 0 ? " cors=" + cors : "")}" +
              $"{(r is null ? "" : $" em {(DateTime.Now - r.At).TotalMilliseconds:0} ms")} {url}");
    }

    // ---------- O retrato: a página como está e um print ----------

    /// <summary>A página como está (sem valores guardados nem o que se digita nos campos), em JSON.</summary>
    private const string DomScript = """
        (() => {
          const o = {}, cut = (s, n) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim().slice(0, n || 200);
          const bare = u => { try { const x = new URL(u, location.href); return x.origin + x.pathname + (x.search ? '?…' : ''); } catch { return cut(u, 120); } };
          o.endereco = bare(location.href); o.titulo = cut(document.title); o.documento = document.readyState;
          o.online = navigator.onLine; o.visibilidade = document.visibilityState;
          o.janela = innerWidth + 'x' + innerHeight + ' zoom ' + devicePixelRatio;
          const p = document.getElementById('carga-pct'), t = document.getElementById('carga-etapa');
          o.carga = p || t ? cut((p ? p.textContent + '% ' : '') + (t ? t.textContent : '')) : null;
          try { const f = window.__deckBonus && window.__deckBonus.summary(); o.socketDoJogo = f ? (f.connected ? 'conectado' : 'não conectado') : 'sem o script do bônus'; }
          catch (e) { o.socketDoJogo = 'erro ' + cut(e.message); }
          const shown = e => { const s = getComputedStyle(e), r = e.getBoundingClientRect();
            return s.display !== 'none' && s.visibility !== 'hidden' && +s.opacity > 0 && r.width * r.height > innerWidth * innerHeight * 0.02; };
          o.aVista = [...document.querySelectorAll('[id]')].filter(shown).map(e => e.id).slice(0, 40);
          const desc = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '')
            + (typeof e.className === 'string' && e.className.trim() ? '.' + e.className.trim().split(/\s+/).slice(0, 3).join('.') : '');
          const chain = []; for (let e = document.elementFromPoint(innerWidth / 2, innerHeight / 2); e && chain.length < 6; e = e.parentElement) chain.push(desc(e));
          o.noMeioDaTela = chain.join(' < ');
          o.texto = cut(document.body ? document.body.innerText : '', 700);
          o.iframes = [...document.querySelectorAll('iframe')].map(f => bare(f.src || 'about:blank')).slice(0, 10);
          o.scripts = [...document.scripts].map(s => s.src ? bare(s.src) : 'inline').slice(0, 40);
          const nav = performance.getEntriesByType('navigation')[0];
          if (nav) o.navegacao = { tipo: nav.type, status: nav.responseStatus, protocolo: nav.nextHopProtocol,
            primeiroByteMs: Math.round(nav.responseStart), domMs: Math.round(nav.domContentLoadedEventEnd), loadMs: Math.round(nav.loadEventEnd) };
          const res = performance.getEntriesByType('resource');
          o.recursos = res.length;
          o.recursosComErro = res.filter(r => r.responseStatus >= 400).slice(0, 15).map(r => r.responseStatus + ' ' + bare(r.name));
          o.maisLentos = res.slice().sort((a, b) => b.duration - a.duration).slice(0, 6).map(r => Math.round(r.duration) + ' ms ' + bare(r.name));
          try { const k = Object.keys(localStorage); o.localStorage = k.length + ' chaves: ' + k.slice(0, 60).join(', '); }
          catch (e) { o.localStorage = 'erro ' + cut(e.message); }
          o.serviceWorker = navigator.serviceWorker ? (navigator.serviceWorker.controller ? 'controlando a página' : 'nenhum') : 'sem suporte';
          if (performance.memory) o.memoriaJs = Math.round(performance.memory.usedJSHeapSize / 1048576) + ' de ' + Math.round(performance.memory.jsHeapSizeLimit / 1048576) + ' MB';
          try { const cv = document.createElement('canvas'), g = cv.getContext('webgl2') || cv.getContext('webgl');
            if (!g) o.webgl = 'sem WebGL';
            else { const d = g.getExtension('WEBGL_debug_renderer_info');
              o.webgl = (typeof WebGL2RenderingContext !== 'undefined' && g instanceof WebGL2RenderingContext ? 'webgl2 ' : 'webgl ')
                + cut(d ? g.getParameter(d.UNMASKED_RENDERER_WEBGL) : g.getParameter(g.RENDERER), 120);
              const l = g.getExtension('WEBGL_lose_context'); if (l) l.loseContext(); } }
          catch (e) { o.webgl = 'erro ' + cut(e.message); }
          o.navegador = navigator.userAgent; o.idioma = navigator.language;
          return JSON.stringify(o);
        })()
        """;

    /// <summary>Anota o retrato da página e salva um print dela (diagnostico\contaN-data.jpg). Cada parte espera no máximo 5 s.</summary>
    public async Task SnapshotAsync(CoreWebView2 core, string why)
    {
        Write($"retrato ({why}):");
        try
        {
            var run = core.ExecuteScriptAsync(DomScript);
            if (await Task.WhenAny(run, Task.Delay(5000)) != run)
                Write("  a página não respondeu em 5 s (travada?)");
            else if (JsonSerializer.Deserialize<string>(await run) is string json)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var field in doc.RootElement.EnumerateObject())
                    Write($"  {field.Name}: {(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText())}");
            }
        }
        catch (Exception ex)
        {
            Write("  não consegui ler a página: " + ex.Message);
        }
        try
        {
            var image = new MemoryStream();
            var shot = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, image);
            if (await Task.WhenAny(shot, Task.Delay(5000)) != shot)
            {
                Write("  o print não veio em 5 s");
                return;
            }
            await shot;
            Directory.CreateDirectory(_folder);
            string file = $"{_profile}-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(_folder, file), image.ToArray());
            Write("  print: " + file);
            foreach (var old in Directory.GetFiles(_folder, _profile + "-*.jpg").OrderByDescending(f => f).Skip(MaxShots))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            Write("  não consegui o print: " + ex.Message);
        }
    }

    /// <summary>
    /// O script da página (antes dos outros do deck): os erros de JavaScript, as promessas rejeitadas sem tratamento, o
    /// console.error, os arquivos que não carregaram e os momentos da página, mandados ao deck na hora (se a página travar
    /// depois, o que veio antes já está no log). A mesma mensagem só 3 vezes, e no máximo 200 por página.
    /// </summary>
    public const string PageScript = """
        (() => {
          if (window !== window.top || window.__deckDiag) return;
          window.__deckDiag = true;
          const t0 = performance.now(), seen = new Map(); let n = 0;
          const bare = u => { try { const x = new URL(u, location.href); return x.origin + x.pathname + (x.search ? '?…' : ''); } catch { return String(u).slice(0, 120); } };
          const send = (kind, text) => {
            text = String(text).replace(/\s+/g, ' ').slice(0, 400);
            const key = kind + text, c = (seen.get(key) || 0) + 1; seen.set(key, c);
            if (c > 3 || ++n > 200) return;
            try { chrome.webview.postMessage('deckdiag:' + kind + ' ' + Math.round(performance.now() - t0) + ' ms: ' + text); } catch { }
          };
          const show = x => { try { return x instanceof Error ? (x.stack || x.message) : typeof x === 'object' ? JSON.stringify(x).slice(0, 300) : String(x); } catch { return String(x); } };
          addEventListener('error', e => {
            const el = e.target;
            if (el && el !== window && (el.src || el.href)) { send('recurso', (el.tagName || '').toLowerCase() + ' não carregou ' + bare(el.src || el.href)); return; }
            send('erro', (e.error && e.error.stack ? e.error.stack : e.message) + ' @ ' + bare(e.filename || '') + ':' + e.lineno + ':' + e.colno);
          }, true);
          addEventListener('unhandledrejection', e => send('promessa', show(e.reason)));
          const error = console.error;
          console.error = function (...args) { try { send('console', args.map(show).join(' ')); } catch { } return error.apply(this, args); };
          document.addEventListener('DOMContentLoaded', () => send('momento', 'DOMContentLoaded'));
          addEventListener('load', () => send('momento', 'load'));
          addEventListener('offline', () => send('rede', 'o navegador ficou offline'));
          addEventListener('online', () => send('rede', 'o navegador voltou a ficar online'));
        })();
        """;
}

/// <summary>O diagnóstico do deck inteiro: o deck.log (abrir, erros) e o .zip para mandar, com o PC, a rede e as contas.</summary>
internal static class Diag
{
    /// <summary>A pasta do diagnóstico (diagnostico, na pasta do deck; a do autoteste é outra).</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeIdleMulti", "diagnostico");

    private static readonly Dictionary<string, int> Seen = new();

    /// <summary>Uma linha no deck.log (a mesma mensagem só 5 vezes por abertura do deck).</summary>
    public static void Write(string text)
    {
        lock (Seen)
        {
            int count = Seen.TryGetValue(text, out int c) ? c + 1 : 1;
            Seen[text] = count;
            if (count > 5)
                return;
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, "deck.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(1000));
                File.AppendAllText(path, $"{DateTime.Now:dd/MM/yy HH:mm:ss}  {text}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>O endereço sem o "?…" e o "#…" (onde poderia ir um token).</summary>
    public static string Bare(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Cut(url, 120);
        if (uri.Scheme is "about" or "data")
            return uri.Scheme + ":" + Cut(uri.AbsolutePath, 40);
        return uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 1 ? "?…" : "");
    }

    public static string Cut(string? text, int max)
    {
        string flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>"certo", ou quanto o relógio do PC está adiantado ou atrasado (o Cloudflare e o login reclamam de relógio errado).</summary>
    public static string Skew(TimeSpan skew) =>
        Math.Abs(skew.TotalSeconds) < 30 ? "certo"
        : $"{(skew > TimeSpan.Zero ? "ADIANTADO" : "ATRASADO")} {Math.Abs(skew.TotalMinutes):0.#} min";

    // ---------- O PC ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static string? Reg(RegistryKey root, string key, string name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            return k?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string Gb(double bytes) => $"{bytes / (1024d * 1024 * 1024):0.0} GB";

    /// <summary>O exe do deck: o caminho, a data (as versões são por data) e o tamanho.</summary>
    public static string Exe()
    {
        string path = Environment.ProcessPath ?? "?";
        try
        {
            var info = new FileInfo(path);
            return $"{path} ({info.LastWriteTime:dd/MM/yy HH:mm}, {info.Length / 1024 / 1024} MB)";
        }
        catch
        {
            return path;
        }
    }

    public static string Windows()
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var lm = Registry.LocalMachine;
        return $"{Reg(lm, nt, "ProductName")} {Reg(lm, nt, "DisplayVersion")} build {Reg(lm, nt, "CurrentBuild")}.{Reg(lm, nt, "UBR")} " +
               $"({(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)";
    }

    /// <summary>A linha do deck.log ao abrir o deck.</summary>
    public static string Summary(CoreWebView2Environment? env) =>
        $"deck aberto: {Exe()} · {Windows()} · WebView2 {env?.BrowserVersionString ?? "?"}";

    /// <summary>O PC: Windows, processador, memória, disco, placa de vídeo, telas, idioma, relógio, proxy, hosts e o WebView2.</summary>
    public static List<string> Machine(string? browserVersion, string? userDataFolder, string root)
    {
        var lines = new List<string>
        {
            "Deck: " + Exe(),
            "Windows: " + Windows(),
            $"Processador: {Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim()} " +
            $"({Environment.ProcessorCount} núcleos lógicos)",
        };
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
            lines.Add($"Memória: {Gb(memory.TotalPhys)}, livre {Gb(memory.AvailPhys)} ({memory.Load}% em uso)");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            lines.Add($"Disco {drive.Name}: livre {Gb(drive.AvailableFreeSpace)} de {Gb(drive.TotalSize)}");
        }
        catch
        {
        }
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in display?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? Enumerable.Empty<string>())
            {
                try
                {
                    using var gpu = display!.OpenSubKey(name);
                    if (gpu?.GetValue("DriverDesc") is object desc)
                        lines.Add($"Placa de vídeo: {desc} (driver {gpu.GetValue("DriverVersion")} de {gpu.GetValue("DriverDate")})");
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        foreach (var screen in Screen.AllScreens)
            lines.Add($"Tela: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (principal)" : "")}");
        lines.Add($"Idioma: {CultureInfo.CurrentCulture.Name} / {CultureInfo.CurrentUICulture.Name}; fuso {TimeZoneInfo.Local.Id}; " +
                  $"agora {DateTime.Now:dd/MM/yy HH:mm:ss} (UTC {DateTime.UtcNow:HH:mm:ss})");

        const string inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        var cu = Registry.CurrentUser;
        lines.Add($"Proxy do Windows: {(Reg(cu, inet, "ProxyEnable") == "1" ? "LIGADO " + Reg(cu, inet, "ProxyServer") : "desligado")}" +
                  (Reg(cu, inet, "AutoConfigURL") is { Length: > 0 } pac ? $"; script de proxy {pac}" : ""));
        try
        {
            var hosts = File.ReadAllLines(Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            var game = hosts.Where(l => l.Contains("pokeidle", StringComparison.OrdinalIgnoreCase)
                                        || l.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)).ToList();
            lines.Add($"Arquivo hosts: {hosts.Count} linhas ativas" + (game.Count > 0 ? "; DO JOGO/CLOUDFLARE: " + string.Join(" | ", game) : ""));
        }
        catch
        {
        }

        string available;
        try
        {
            available = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            available = "erro " + ex.Message;
        }
        lines.Add($"WebView2: em uso {browserVersion ?? "?"}, instalado {available}; pasta {userDataFolder}");
        lines.Add($"WebView2 argumentos do deck: {DeckForm.BrowserArguments}");
        foreach (var name in new[] { "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                     "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                lines.Add($"Variável {name}={value}");
        foreach (var (hive, label) in new[] { (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            try
            {
                using var policies = hive.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge\WebView2");
                if (policies is not null)
                    lines.Add($"Políticas do WebView2 ({label}): " +
                              string.Join(", ", policies.GetSubKeyNames().Concat(policies.GetValueNames().Select(v => $"{v}={policies.GetValue(v)}"))));
            }
            catch
            {
            }
        }
        return lines;
    }

    /// <summary>O antivírus que o Windows conhece (o que examina HTTPS pode derrubar o socket do jogo).</summary>
    public static async Task<string> AntivirusAsync()
    {
        try
        {
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | " +
                "ForEach-Object { $_.displayName + ' (estado ' + ('{0:X}' -f $_.productState) + ')' }\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
            var read = process.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(15000)) != read)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
                return "o Windows não respondeu";
            }
            var names = (await read).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.Length > 0 ? string.Join("; ", names) : "nenhum informado";
        }
        catch (Exception ex)
        {
            return "não consegui ver: " + ex.Message;
        }
    }

    /// <summary>A rede fora do navegador do deck: o DNS do jogo e o site (o status, o tempo, o Cloudflare e o relógio).</summary>
    public static async Task<List<string>> NetworkAsync()
    {
        var lines = new List<string>();
        try
        {
            var watch = Stopwatch.StartNew();
            var ips = await Dns.GetHostAddressesAsync("pokeidle.io").WaitAsync(TimeSpan.FromSeconds(10));
            lines.Add($"DNS pokeidle.io: {string.Join(", ", ips.Select(i => i.ToString()))} em {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            lines.Add("DNS pokeidle.io FALHOU: " + ex.GetBaseException().Message);
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PokeIdleDeck-diagnostico/1.0");
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync("https://pokeidle.io/app", HttpCompletionOption.ResponseHeadersRead);
            string Header(string name) => response.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "";
            lines.Add($"HTTPS pokeidle.io/app (fora do navegador; o Cloudflare pode dar 403 aqui, é normal): {(int)response.StatusCode} " +
                      $"{response.ReasonPhrase} HTTP/{response.Version} em {watch.ElapsedMilliseconds} ms; server={Header("server")} cf-ray={Header("cf-ray")}");
            if (response.Headers.Date is DateTimeOffset date)
                lines.Add("Relógio do PC comparado com o do site: " + Skew(DateTimeOffset.UtcNow - date));
        }
        catch (Exception ex)
        {
            lines.Add("HTTPS pokeidle.io FALHOU: " + ex.GetBaseException().Message);
        }
        return lines;
    }

    private static long FolderBytes(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } })
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---------- O arquivo para mandar ----------

    /// <summary>
    /// Junta o diagnóstico num .zip em <paramref name="outputFolder"/>: o relatorio.txt (o PC, a rede, o deck e cada conta
    /// agora, com um retrato e um print das ligadas), os logs das contas, os prints, o deck.log e o aberturas.log.
    /// Sem <paramref name="outside"/> (o autoteste), não consulta o antivírus nem a rede.
    /// </summary>
    public static async Task<string> CreateReportAsync(string outputFolder, CoreWebView2Environment? env, IReadOnlyList<AccountPane> panes,
        IEnumerable<string> deckLines, string root, bool outside)
    {
        var report = new List<string> { $"PokeIdle Deck: diagnóstico de {DateTime.Now:dd/MM/yyyy HH:mm:ss}", "" };
        report.Add("== PC ==");
        // O WebView2 só se lê na thread da janela: antes de ir para o fundo.
        string? version = env?.BrowserVersionString, userData = env?.UserDataFolder;
        report.AddRange(await Task.Run(() => Machine(version, userData, root)));
        if (outside)
            report.Add("Antivírus: " + await AntivirusAsync());
        report.Add("");
        report.Add("== Rede ==");
        if (outside)
            report.AddRange(await NetworkAsync());
        report.Add("");
        report.Add("== Deck ==");
        report.AddRange(deckLines);
        report.Add("");
        foreach (var pane in panes)
        {
            report.Add($"== {pane.ProfileName}: {pane.AccountName} ==");
            report.Add($"{(pane.Off ? "desligada" : "ligada")}; status {pane.Status} {pane.StatusText}; página {Bare(pane.Core?.Source)}");
            report.Add($"recargas do vigia seguidas {pane.StuckReloads}; cargas {pane.Loads}; recarregou limpo {pane.CleanReloads}; dados apagados {pane.Wipes}");
            if (pane.HeaderInfo.Length > 0)
                report.Add("cabeçalho: " + pane.HeaderInfo);
            if (pane.Profile is CoreWebView2Profile profile)
            {
                try
                {
                    var extensions = await profile.GetBrowserExtensionsAsync();
                    report.Add("extensões: " + (extensions.Count == 0 ? "nenhuma"
                        : string.Join(", ", extensions.Select(e => $"{e.Name}{(e.IsEnabled ? "" : " (desligada)")}"))));
                }
                catch (Exception ex)
                {
                    report.Add("extensões: erro " + ex.Message);
                }
            }
            if (userData is not null)
            {
                string profileFolder = Path.Combine(userData, "EBWebView", pane.ProfileName);
                long bytes = await Task.Run(() => FolderBytes(profileFolder));
                report.Add($"perfil no disco: {bytes / 1024 / 1024} MB");
            }
            if (!pane.Off && pane.Core is CoreWebView2 core && pane.Diag is PaneDiag diag)
                await diag.SnapshotAsync(core, "relatório");
            report.Add("");
        }
        foreach (var pane in panes)
            pane.Diag?.Flush();

        Directory.CreateDirectory(outputFolder);
        string zip = Path.Combine(outputFolder, $"PokeIdleDeck-diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(archive.CreateEntry("relatorio.txt").Open(), new UTF8Encoding(true)))
                writer.Write(string.Join(Environment.NewLine, report));
            void Add(string path, string name)
            {
                try
                {
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = archive.CreateEntry(name).Open();
                    source.CopyTo(target);
                }
                catch (IOException)
                {
                }
            }
            if (Directory.Exists(Folder))
                foreach (var file in Directory.GetFiles(Folder))
                    Add(file, "diagnostico/" + Path.GetFileName(file));
            foreach (var name in new[] { "aberturas.log", "consumo.log" })
                if (File.Exists(Path.Combine(root, name)))
                    Add(Path.Combine(root, name), name);
        });
        return zip;
    }
}
