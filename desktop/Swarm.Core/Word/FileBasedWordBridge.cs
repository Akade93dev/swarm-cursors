// P1-W Word Provider T3: FileBasedWordBridge — DocumentFormat.OpenXml 3.1.0 纯托管 .docx 解析
// 零 Office 依赖, 不打开 Word 进程, 直接解析 .docx zip 内的 word/document.xml.
// 能力: ContentControl 文本/ID/Tag, Bookmark 名称/位置, Paragraph Style/文本, 段落层级.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Swarm.Core.Word;

/// <summary>Word 文档解析接口 (T3: 零 Office 依赖, T1 WordBridge 需 Office 实机).</summary>
public interface IWordBridge
{
    /// <summary>解析 .docx 文件, 返回完整结构化视图.</summary>
    WordDocument Parse(string docxPath);

    /// <summary>引擎是否可用 (DocumentFormat.OpenXml 始终 true).</summary>
    bool IsAvailable { get; }
}

/// <summary>Word 文档结构化视图.</summary>
public sealed record WordDocument(
    string FilePath,
    int ParagraphCount,
    IReadOnlyList<ContentControlInfo> ContentControls,
    IReadOnlyList<BookmarkInfo> Bookmarks,
    IReadOnlyList<ParagraphInfo> Paragraphs);

public sealed record ContentControlInfo(
    string Id,
    string? Tag,
    string? Title,
    string Content,
    string? PlaceholderText);

public sealed record BookmarkInfo(
    string Name,
    string Text,
    int ParagraphIndex);

public sealed record ParagraphInfo(
    int Index,
    string StyleName,
    string Text,
    int HeadingLevel); // 0=正文, 1-9=Heading1..9

public sealed class FileBasedWordBridge : IWordBridge
{
    public bool IsAvailable => true;

    public WordDocument Parse(string docxPath)
    {
        if (string.IsNullOrWhiteSpace(docxPath))
            throw new ArgumentException("docxPath required", nameof(docxPath));
        if (!File.Exists(docxPath))
            throw new FileNotFoundException("docx file not found", docxPath);

        using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
        var main = doc.MainDocumentPart?.Document;
        if (main == null)
            return new WordDocument(docxPath, 0, Array.Empty<ContentControlInfo>(), Array.Empty<BookmarkInfo>(), Array.Empty<ParagraphInfo>());

        var body = main.Body!;
        var paragraphs = body.Elements<Paragraph>().ToList();
        var controls = new List<ContentControlInfo>();
        var bookmarks = new List<BookmarkInfo>();
        var paraInfos = new List<ParagraphInfo>(paragraphs.Count);

        for (int i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];
            // Paragraph 基础信息
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
            var text = p.InnerText ?? "";
            var headingLevel = ParseHeadingLevel(style);
            paraInfos.Add(new ParagraphInfo(i, style, text.Trim(), headingLevel));

            // ContentControl 提取 (在 paragraph 内部)
            foreach (var sdt in p.Elements<SdtElement>())
            {
                var properties = sdt.SdtProperties;
                var idRaw = properties?.GetFirstChild<SdtId>()?.Val?.Value;
                var id = idRaw.HasValue ? idRaw.Value.ToString() : "";
                var tag = properties?.GetFirstChild<Tag>()?.Val?.Value;
                var alias = properties?.GetFirstChild<SdtAlias>()?.Val?.Value;
                var ph = properties?.GetFirstChild<SdtPlaceholder>()?.InnerText;
                var content = sdt.InnerText ?? "";
                controls.Add(new ContentControlInfo(id, tag, alias, content.Trim(), ph?.Trim()));
            }

            // Bookmark 提取
            foreach (var bookmarkStart in p.Elements<BookmarkStart>())
            {
                var name = bookmarkStart.Name?.Value ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                var bmText = bookmarkStart.InnerText ?? "";
                bookmarks.Add(new BookmarkInfo(name, bmText.Trim(), i));
            }
        }

        return new WordDocument(docxPath, paragraphs.Count, controls, bookmarks, paraInfos);
    }

    private static int ParseHeadingLevel(string styleName)
    {
        // Word 默认: Heading1 → 1, heading 1 → 1, Title → 0 (正文).
        // 兼容中英两种命名.
        var s = styleName.ToLowerInvariant().Trim();
        if (s.StartsWith("heading"))
        {
            var num = s.AsSpan(7).Trim();
            if (int.TryParse(num, out var level) && level >= 1 && level <= 9) return level;
        }
        if (s.StartsWith("标题")) // Word 中文本地化
        {
            return s.Length > 2 && int.TryParse(s.AsSpan(2), out var level) ? Math.Clamp(level, 1, 9) : 0;
        }
        return 0;
    }
}
