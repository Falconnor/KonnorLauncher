using System.Net;
using Markdig;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Descripción de un proyecto de Modrinth (Markdown que a menudo lleva HTML) convertida en una página
/// con el estilo del launcher. La política de contenido no deja ejecutar scripts, ni cargar marcos,
/// formularios o recursos que no sean imágenes y vídeos por HTTPS.
/// </summary>
public static class MarkdownPage
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tablas, listas de tareas, enlaces automáticos, notas...
        .Build();

    private const string ContentPolicy =
        "default-src 'none'; img-src https: data:; media-src https:; style-src 'unsafe-inline'; " +
        "frame-src 'none'; form-action 'none'; base-uri 'none'";

    public static string Render(string? markdown, string title)
    {
        var body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        return $"""
            <!DOCTYPE html>
            <html lang="es">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{ContentPolicy}">
            <title>{WebUtility.HtmlEncode(title)}</title>
            <style>{Css}</style>
            </head>
            <body>{body}</body>
            </html>
            """;
    }

    // Mismos tokens que el launcher: tinta clara sobre cristal oscuro y el acento (azul) en los enlaces.
    private const string Css = """
        :root { color-scheme: dark; }
        html, body { background: transparent; margin: 0; }
        body {
          padding: 4px 12px 28px 2px;
          color: #F3F5F7; font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", sans-serif;
          word-wrap: break-word; overflow-wrap: anywhere;
        }
        h1, h2, h3, h4 { font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif; line-height: 1.25; margin: 1.4em 0 .5em; }
        h1 { font-size: 26px; } h2 { font-size: 21px; } h3 { font-size: 17px; } h4 { font-size: 15px; }
        h1:first-child, h2:first-child, h3:first-child, p:first-child { margin-top: 0; }
        p, ul, ol, table, blockquote, pre { margin: 0 0 1em; }
        a { color: #4A88FF; text-decoration: none; }
        a:hover { color: #7AA8FF; text-decoration: underline; }
        img, video { max-width: 100%; height: auto; border-radius: 10px; }
        a img { border-radius: 4px; }
        hr { border: 0; border-top: 1px solid rgba(255,255,255,.12); margin: 1.6em 0; }
        code { background: rgba(255,255,255,.08); border-radius: 6px; padding: 1px 6px; font: 13px Consolas, monospace; }
        pre { background: rgba(0,0,0,.28); border: 1px solid rgba(255,255,255,.08); border-radius: 12px; padding: 12px 14px; overflow: auto; }
        pre code { background: none; padding: 0; }
        blockquote { border-left: 3px solid rgba(52,120,246,.7); padding: 2px 0 2px 14px; color: rgba(243,245,247,.75); }
        table { border-collapse: collapse; }
        th, td { border: 1px solid rgba(255,255,255,.12); padding: 6px 10px; }
        th { background: rgba(255,255,255,.06); }
        details { background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.08); border-radius: 12px; padding: 8px 14px; margin-bottom: 1em; }
        summary { cursor: pointer; font-weight: 600; }
        ::selection { background: #3478F6; }
        /* La barra de desplazamiento la pone el launcher: la de la página no se ve. */
        html { scrollbar-width: none; }
        ::-webkit-scrollbar { width: 0; height: 0; }
        """;
}
