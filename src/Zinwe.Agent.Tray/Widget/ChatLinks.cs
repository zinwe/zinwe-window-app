using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Zinwe.Agent.Tray.Widget;

public enum ChatLinkKind
{
    Text,
    Url,
    Email,
    Phone,
}

/// <summary>One piece of a chat line: plain text, or a link / email / phone number with what opening it does.</summary>
public sealed record ChatLinkToken(ChatLinkKind Kind, string Text, string Target = "");

/// <summary>
/// Finds links, email addresses and phone numbers in chat text (FR-12), the same rules as the dashboard
/// (zinwe-frontend src/lib/linkify.ts). Only http(s) and mailto: targets come out; a phone number is copied, not dialled.
/// </summary>
public static partial class ChatLinks
{
    [GeneratedRegex("""
        (?<url>\bhttps?://[^\s<>"'`]+|\bwww\.[^\s<>"'`]+)
        |(?<email>\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b)
        |(?<phone>(?<![\w+/.-])\+?\d(?:[\s-]?\d){9,14}(?![\w/.-]?\d))
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    private static readonly char[] Trailing = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"'];

    public static IReadOnlyList<ChatLinkToken> Tokens(string? text)
    {
        var tokens = new List<ChatLinkToken>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }
        var last = 0;
        foreach (Match m in Pattern().Matches(text))
        {
            var value = m.Value;
            ChatLinkToken token;
            if (m.Groups["url"].Success)
            {
                value = value.TrimEnd(Trailing);
                token = new(ChatLinkKind.Url, value, value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value);
            }
            else if (m.Groups["email"].Success)
            {
                token = new(ChatLinkKind.Email, value, "mailto:" + value);
            }
            else
            {
                token = new(ChatLinkKind.Phone, value, value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal));
            }
            if (m.Index > last)
            {
                tokens.Add(new(ChatLinkKind.Text, text[last..m.Index]));
            }
            tokens.Add(token);
            last = m.Index + value.Length;
        }
        if (last < text.Length)
        {
            tokens.Add(new(ChatLinkKind.Text, text[last..]));
        }
        return tokens;
    }

    // --- XAML: <TextBlock w:ChatLinks.Text="{Binding Text}" /> -------------------------------------------

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ChatLinks), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }
        block.Inlines.Clear();
        foreach (var t in Tokens(e.NewValue as string))
        {
            block.Inlines.Add(t.Kind == ChatLinkKind.Text ? new Run(t.Text) : LinkFor(t));
        }
    }

    private static Hyperlink LinkFor(ChatLinkToken t)
    {
        var link = new Hyperlink(new Run(t.Text))
        {
            Foreground = (Application.Current?.TryFindResource("Zinwe.Ink") as System.Windows.Media.Brush)
                         ?? System.Windows.Media.Brushes.Black,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            ToolTip = t.Kind == ChatLinkKind.Phone ? "Click to copy the number" : "Click to open · right-click to copy",
        };
        var copy = new MenuItem { Header = t.Kind switch
        {
            ChatLinkKind.Url => "Copy link",
            ChatLinkKind.Email => "Copy email address",
            _ => "Copy number",
        } };
        copy.Click += (_, _) => Copy(t.Text);
        link.ContextMenu = new ContextMenu { Items = { copy } };
        link.Click += (_, _) =>
        {
            if (t.Kind == ChatLinkKind.Phone)
            {
                Copy(t.Target);
                link.ToolTip = "Copied";
                return;
            }
            Open(t.Target);
        };
        return link;
    }

    private static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy (another app holds it); the right-click Copy can be tried again.
        }
    }

    private static void Open(string target)
    {
        // Only web links and email: never a file path or another scheme typed into a chat.
        if (!target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser / mail app registered: the right-click Copy still works.
        }
    }
}
