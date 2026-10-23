using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Hover-revealed buttons (Opacity="0" until the pointer enters the row)
/// must opt out of the tab sequence: opacity hides the focus visual too, so
/// a tab stop there is fully invisible yet actionable — the 2026-10-19
/// review found Space ejecting a drive from an unseen focused eject button
/// (no confirmation on that path). The bin-clear button shares the pattern
/// (its confirmation dialog mitigates but does not fix the confusion), and
/// AttachmentTileStrip's RemoveAttachmentButton shows the intended
/// IsTabStop="False" form.
/// </summary>
public sealed class HoverRevealButtonTabStopContractTests
{
    [Fact]
    public void EveryZeroOpacityButton_OptsOutOfTabSequence()
    {
        string srcRoot = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        var offenders = new List<string>();
        // A Button opening tag (attributes may span lines) that carries
        // Opacity="0".
        var buttonRegex = new Regex(
            @"<Button\b[^>]*Opacity=""0""[^>]*>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        foreach (string path in Directory.EnumerateFiles(
                     srcRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xaml = File.ReadAllText(path);
            foreach (Match match in buttonRegex.Matches(xaml))
            {
                if (!match.Value.Contains("IsTabStop=\"False\"", StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{Path.GetRelativePath(srcRoot, path)}: " +
                        $"{Regex.Match(match.Value, "x:Name=\\\"([^\\\"]+)\\\"").Groups[1].Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero-opacity hover buttons are invisible-but-focusable without " +
            "IsTabStop=\"False\" (Space then activates an unseen button):\n  " +
            string.Join("\n  ", offenders));
    }
}
