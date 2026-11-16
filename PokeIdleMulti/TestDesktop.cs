using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PokeIdleMulti;

/// <summary>
/// Os testes (<c>--self-test</c> e <c>--consumo</c>) abrem na Área de trabalho 2 do Windows (Win+Ctrl+→), longe da tela
/// onde o usuário joga, e ninguém minimiza a janela no meio do teste. Usa a API documentada (IVirtualDesktopManager),
/// que só move janelas do próprio processo: um gancho de eventos pega cada janela de primeiro nível que aparece e
/// manda para lá. As com dono (Recompra, Registro, Resgate em lote, popups) vão junto com o
/// deck. A janela das lives não tem área nenhuma (sem barra de tarefas, o WinForms dá a ela um dono escondido, e
/// janela assim aparece em qualquer área): o Windows não move, então no autoteste ela fica escondida pelo DWM
/// (DWMWA_CLOAK, o mesmo estado de uma janela de outra área: continua sendo desenhada e tocando, só não aparece); no
/// teste de consumo ela nunca sai de fora da tela, e fica como no deck. Sem a Área 2, as janelas ficam onde abriram
/// (no 2º monitor, sem pegar o foco), como antes.
/// </summary>
internal static class TestDesktop
{
    private static IVirtualDesktopManager? _manager;
    private static Guid _target;
    private static WinEventProc? _proc;
    private static IntPtr _hook;
    private static bool _hideStrays;
    private static System.Windows.Forms.Timer? _retry;
    private static readonly Dictionary<IntPtr, int> Pending = new();
    private static readonly HashSet<IntPtr> Moved = new();
    private static readonly HashSet<IntPtr> WithOwner = new();
    private static readonly Dictionary<IntPtr, string> Cloaked = new();

    /// <summary>O gancho está ligado e a Área de trabalho 2 existe.</summary>
    public static bool Active => _hook != IntPtr.Zero;

    /// <summary>Para onde foram as janelas do teste (relatório).</summary>
    public static string Summary => !Active
        ? "sem Área de trabalho 2 (no 2º monitor)"
        : $"Área de trabalho 2: {Moved.Count} movidas, {WithOwner.Count} junto com o dono"
          + (Cloaked.Count > 0 ? $", {Cloaked.Count} sem área escondidas ({string.Join(", ", Cloaked.Values.Distinct())})" : "");

    /// <summary>A janela está na Área de trabalho 2.</summary>
    public static bool IsOnTarget(IntPtr hwnd) => _manager is not null
        && _manager.GetWindowDesktopId(hwnd, out var id) == 0 && id == _target;

    /// <summary>
    /// Minimiza ou restaura sem ativar a janela. O <c>Form.WindowState</c> ativa, e uma janela ativada na Área 2 pode
    /// trazer a área para a tela (e tirar o foco de quem joga).
    /// </summary>
    public static void SetMinimized(Form form, bool minimized) => ShowWindow(form.Handle, minimized ? SwShowMinNoActive : SwShowNoActivate);

    /// <summary>
    /// Esconde a janela principal do teste desde antes de aparecer até ela chegar na Área 2: ela aparece antes de o
    /// gancho ter vez (o deck ainda está ocupado abrindo) e piscaria uns 50 ms no 2º monitor.
    /// </summary>
    public static void HideUntilMoved(Form form)
    {
        if (!Active)
            return;
        if (form.IsHandleCreated)
            SetCloak(form.Handle, true);
        else
            form.HandleCreated += (_, _) => SetCloak(form.Handle, true);
    }

    /// <summary>
    /// Liga o gancho no thread da interface (antes do <c>Application.Run</c>). Falso: sem Área 2. Sem
    /// <paramref name="hideStrays"/>, a janela sem área fica como está (o teste de consumo mede as lives como no deck:
    /// estacionadas fora da tela, sem esconder).
    /// </summary>
    public static bool Start(bool hideStrays = true)
    {
        if (Active || Desktop2() is not Guid target)
            return Active;
        _hideStrays = hideStrays;
        try
        {
            _manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ManagerClsid, true)!)!;
        }
        catch
        {
            return false;
        }
        _target = target;
        _proc = OnShow;
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _proc, (uint)Environment.ProcessId, 0, 0);
        if (_hook == IntPtr.Zero)
            return false;
        // O Windows registra a janela recém-mostrada um instante depois: até lá a mudança falha e tenta de novo.
        _retry = new System.Windows.Forms.Timer { Interval = 50 };
        _retry.Tick += (_, _) => RetryPending();
        return true;
    }

    /// <summary>O identificador da 2ª área de trabalho, na ordem do Win+Ctrl+→ (null: só tem uma).</summary>
    private static Guid? Desktop2()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            return key?.GetValue("VirtualDesktopIDs") is byte[] { Length: >= 32 } ids ? new Guid(ids.AsSpan(16, 16)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Só a própria janela (não um item dela) e só as de primeiro nível: as filhas vão junto.
        if (idObject != ObjidWindow || idChild != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, GaRoot) != hwnd)
            return;
        if (TryMove(hwnd))
        {
            if (!Cloaked.ContainsKey(hwnd))
                SetCloak(hwnd, false);
            return;
        }
        // Enquanto espera, escondida (sem piscar na tela).
        SetCloak(hwnd, true);
        Pending[hwnd] = 0;
        _retry!.Start();
    }

    private static void RetryPending()
    {
        foreach (var hwnd in Pending.Keys.ToList())
        {
            if (!IsWindowVisible(hwnd) || TryMove(hwnd))
            {
                Pending.Remove(hwnd);
                if (!Cloaked.ContainsKey(hwnd))
                    SetCloak(hwnd, false);
            }
            // 1 s sem conseguir: a janela de título continua escondida; dica e menu (sem título) voltam a aparecer.
            else if ((Pending[hwnd] += 1) >= 20)
            {
                Pending.Remove(hwnd);
                if (_hideStrays && HasCaption(hwnd))
                    Cloaked[hwnd] = TitleOf(hwnd);
                else
                    SetCloak(hwnd, false);
            }
        }
        if (Pending.Count == 0)
            _retry!.Stop();
    }

    private static bool TryMove(IntPtr hwnd)
    {
        if (IsOnTarget(hwnd))
            return true;
        // Janela com dono vai junto com o dono; a de dono escondido (sem barra de tarefas) não tem área: esconde.
        var owner = GetWindow(hwnd, GwOwner);
        if (owner != IntPtr.Zero && IsOnTarget(owner))
        {
            WithOwner.Add(hwnd);
            return true;
        }
        if (owner != IntPtr.Zero && !IsWindowVisible(owner))
        {
            Cloak(hwnd);
            return true;
        }
        if (_manager!.MoveWindowToDesktop(hwnd, ref _target) != 0 || !IsOnTarget(hwnd))
            return false;
        Moved.Add(hwnd);
        return true;
    }

    /// <summary>Esconde de vez uma janela de título que não tem como ir para a Área 2.</summary>
    private static void Cloak(IntPtr hwnd)
    {
        if (_hideStrays && HasCaption(hwnd) && !Cloaked.ContainsKey(hwnd) && SetCloak(hwnd, true))
            Cloaked[hwnd] = TitleOf(hwnd);
    }

    private static bool HasCaption(IntPtr hwnd) => (GetWindowLong(hwnd, GwlStyle) & WsCaption) == WsCaption;

    private static string TitleOf(IntPtr hwnd)
    {
        var title = new StringBuilder(256);
        return GetWindowText(hwnd, title, title.Capacity) > 0 ? title.ToString() : "?";
    }

    private static bool SetCloak(IntPtr hwnd, bool on)
    {
        int value = on ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    private static readonly Guid ManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private const uint EventObjectShow = 0x8002;
    private const int ObjidWindow = 0;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int SwShowNoActivate = 4;
    private const int SwShowMinNoActive = 7;
    private const int DwmwaCloak = 13;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
