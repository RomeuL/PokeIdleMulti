using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = Theme.Renderer;
        // O que derruba o deck, ou a tarefa que falhou sem ninguém ver, vai para o diagnostico\deck.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Write("ERRO que fechou o deck: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Diag.Write("erro numa tarefa: " + e.Exception.GetBaseException());

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Diag.Write("sem o WebView2 Runtime instalado");
            DeckBox.Show(null,
                "Falta o WebView2 Runtime da Microsoft (o motor de navegador que o deck usa).\n\n" +
                "Baixe em https://go.microsoft.com/fwlink/p/?LinkId=2124703 e abra o deck de novo.",
                "PokeIdle Deck", DeckBoxIcon.Error);
            return;
        }

        // Teste de consumo das lives: perfil de teste à parte, roda junto com o deck aberto.
        if (args.Contains("--consumo"))
        {
            using var measuring = new Mutex(true, "PokeIdleDeck-consumo", out bool alone);
            if (alone)
            {
                // A janela do teste na Área de trabalho 2; as lives ficam como no deck (fora da tela), para a medição valer.
                TestDesktop.Start(hideStrays: false);
                var test = new ConsumptionTest();
                TestDesktop.HideUntilMoved(test);
                Application.Run(test);
            }
            return;
        }

        bool selfTest = args.Contains("--self-test");
        // Dois decks abertos com as mesmas contas ficam se derrubando ("Account opened elsewhere").
        using var single = new Mutex(true, selfTest ? "PokeIdleDeck-selftest" : "PokeIdleDeck", out bool first);
        if (!first)
        {
            DeckBox.Show(null, "O PokeIdle Deck já está aberto.", "PokeIdle Deck");
            return;
        }

        // O autoteste abre na Área de trabalho 2 do Windows (sem ela, no 2º monitor), longe de quem está jogando.
        if (selfTest)
            TestDesktop.Start();
        var deck = new DeckForm(selfTest);
        TestDesktop.HideUntilMoved(deck);
        Application.Run(deck);
    }
}
