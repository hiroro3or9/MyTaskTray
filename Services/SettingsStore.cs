using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using MyTaskTray.Models;

namespace MyTaskTray.Services
{
    /// <summary>
    /// 設定を JSON ファイルとして読み書きする。
    /// 保存先: %APPDATA%\MyTaskTray\settings.json
    /// </summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // 日本語をエスケープせずそのまま書き出す（手動編集しやすくするため）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>設定ファイルを置くフォルダー。</summary>
        public static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyTaskTray");

        /// <summary>設定ファイルのフルパス。</summary>
        public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

        /// <summary>読み取りに失敗したときの再試行回数と間隔。</summary>
        private const int MaxReadAttempts = 3;
        private const int ReadRetryDelayMs = 100;

        /// <summary>
        /// 設定を読み込む。ファイルが無い場合は既定値を作成して保存する。
        /// 壊れている場合は既定値を返し、元ファイルを .bak に退避する。
        /// 一時的に読めなかっただけの場合は少し待って読み直す。
        /// それでも読めなければ、破損とは区別して退避も上書きもせず、
        /// <see cref="AppSettings.IsFallback"/> を立てた空の設定を返す。
        /// </summary>
        public static AppSettings Load()
        {
            if (!File.Exists(FilePath))
            {
                AppSettings created = AppSettings.CreateDefault();
                EnsureIds(created);

                try
                {
                    Save(created);
                }
                catch (Exception)
                {
                    // 書き込めなくても、この起動のあいだは既定値で動かす
                    created.IsFallback = true;
                }

                return created;
            }

            string? json = TryReadFile();
            if (json is null)
            {
                // ウイルス対策や同期ソフトによる一時的なロックが続いている状態。
                // 壊れているとは限らないため、退避はせず「上書きしない印」だけ付ける
                return new AppSettings { IsFallback = true };
            }

            AppSettings? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return BackupAndCreateDefault();
            }

            if (loaded is null)
            {
                return BackupAndCreateDefault();
            }

            loaded.Items ??= [];

            // 手で書き足した項目には Id が無い。ここで採番して書き戻しておかないと、
            // 読むたびに別の Id になり、連番の引き継ぎ（Id での突き合わせ）が働かない
            if (EnsureIds(loaded))
            {
                try
                {
                    Save(loaded);
                }
                catch (Exception)
                {
                    // 採番を保存できなくても、この起動のあいだは動作する
                }
            }

            return loaded;
        }

        /// <summary>
        /// 壊れたファイルを .bak に退避し、既定値を返す。
        /// 退避してあるので、既定値をそのまま保存してしまっても元の内容は取り戻せる。
        /// </summary>
        private static AppSettings BackupAndCreateDefault()
        {
            TryBackupBrokenFile();

            AppSettings created = AppSettings.CreateDefault();
            EnsureIds(created);
            return created;
        }

        /// <summary>
        /// ファイルを読む。ロックされている場合は少し待って再試行する。
        /// 読めなかった場合は null。
        /// </summary>
        private static string? TryReadFile()
        {
            for (int attempt = 1; attempt <= MaxReadAttempts; attempt++)
            {
                try
                {
                    return File.ReadAllText(FilePath, Encoding.UTF8);
                }
                catch (Exception) when (attempt < MaxReadAttempts)
                {
                    Thread.Sleep(ReadRetryDelayMs);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>Id が空の項目に採番する。1 件でも採番したら true。</summary>
        private static bool EnsureIds(AppSettings settings)
        {
            bool assigned = false;

            foreach (ClipItem item in settings.Items)
            {
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = ClipItem.NewId();
                    assigned = true;
                }
            }

            return assigned;
        }

        /// <summary>設定を保存する。書き込みは一時ファイル経由で行い、破損を避ける。</summary>
        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(DirectoryPath);

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tempPath = FilePath + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, null);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
        }

        private static void TryBackupBrokenFile()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + ".bak", true);
                }
            }
            catch (Exception)
            {
                // 退避に失敗しても処理は続行する
            }
        }
    }
}
