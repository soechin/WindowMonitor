using System.IO;
using OpenCvSharp;

namespace WindowMonitor.Matching;

/// <summary>
/// 樣板資料夾。列舉資料夾裡的影像檔並載入成 <see cref="TemplateItem"/>，
/// 同時負責 Mat 的生命週期——重新載入時舊的必須確實釋放。
/// </summary>
public sealed class TemplateLibrary : IDisposable
{
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>沒有另行指定時使用的資料夾，位置跟著執行檔走。</summary>
    public static readonly string DefaultFolderPath = Path.Combine(AppContext.BaseDirectory, "Templates");

    private readonly Lock _sync = new();

    private TemplateItem[] _items = [];
    private string[] _loadErrors = [];

    public TemplateLibrary()
    {
        FolderPath = DefaultFolderPath;
    }

    public string FolderPath { get; private set; }

    public bool IsDefaultFolder =>
        string.Equals(FolderPath, DefaultFolderPath, StringComparison.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Length;
            }
        }
    }

    /// <summary>載入失敗的檔案與原因，供 UI 顯示。</summary>
    public IReadOnlyList<string> LoadErrors
    {
        get
        {
            lock (_sync)
            {
                return _loadErrors;
            }
        }
    }

    /// <summary>目前已載入的樣板快照，只用來顯示清單。比對請走 <see cref="ForEach"/>。</summary>
    public IReadOnlyList<TemplateItem> Snapshot()
    {
        lock (_sync)
        {
            return _items;
        }
    }

    public void SetFolder(string path)
    {
        FolderPath = path;
    }

    public bool FolderExists => Directory.Exists(FolderPath);

    public void EnsureFolder()
    {
        Directory.CreateDirectory(FolderPath);
    }

    /// <summary>
    /// 在持有鎖的情況下走訪所有樣板。比對期間 <see cref="Reload"/> 會被擋住，
    /// 因此不會發生「正在比對的 Mat 被釋放」。
    /// </summary>
    public void ForEach(Action<TemplateItem> action)
    {
        lock (_sync)
        {
            foreach (TemplateItem item in _items)
            {
                action(item);
            }
        }
    }

    public void ForgetPositions()
    {
        lock (_sync)
        {
            foreach (TemplateItem item in _items)
            {
                item.ForgetPosition();
            }
        }
    }

    /// <summary>
    /// 重新列舉並載入資料夾。先把新的全部載完才換掉舊的——
    /// 中途某一張壞掉不該把原本能用的樣板一起清空。
    /// </summary>
    public void Reload()
    {
        List<TemplateItem> loaded = [];
        List<string> errors = [];

        if (!Directory.Exists(FolderPath))
        {
            errors.Add($"找不到資料夾：{FolderPath}");
        }
        else
        {
            foreach (string path in EnumerateImageFiles())
            {
                TryLoad(path, loaded, errors);
            }
        }

        TemplateItem[] replaced;
        lock (_sync)
        {
            replaced = _items;
            _items = [.. loaded];
            _loadErrors = [.. errors];
        }

        // 換手已在鎖內完成，此時舊的樣板不可能還被任何人走訪到
        foreach (TemplateItem item in replaced)
        {
            item.Dispose();
        }
    }

    private IEnumerable<string> EnumerateImageFiles()
    {
        return Directory.EnumerateFiles(FolderPath)
            .Where(path => SupportedExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryLoad(string path, List<TemplateItem> loaded, List<string> errors)
    {
        string fileName = Path.GetFileName(path);

        try
        {
            // 一律走 ImDecode 而非 ImRead：ImRead 把路徑交給原生層以 ANSI 處理，
            // 只要路徑或檔名含中文就會靜默失敗（回傳空 Mat）。
            // 灰階載入：比對只看形狀，彩色會讓 MatchTemplate 多算兩個通道。
            // 樣板與畫面必須同為單通道，見 TemplateMatcher.ConvertToGray。
            byte[] bytes = File.ReadAllBytes(path);
            Mat image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);

            if (image.Empty())
            {
                image.Dispose();
                errors.Add($"{fileName}：不是有效的影像檔");
                return;
            }

            loaded.Add(new TemplateItem(Path.GetFileNameWithoutExtension(path), path, image));
        }
        catch (Exception ex)
        {
            errors.Add($"{fileName}：{ex.Message}");
        }
    }

    public void Dispose()
    {
        TemplateItem[] items;
        lock (_sync)
        {
            items = _items;
            _items = [];
        }

        foreach (TemplateItem item in items)
        {
            item.Dispose();
        }
    }
}
