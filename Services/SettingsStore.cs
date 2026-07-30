using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

        /// <summary>
        /// 設定を読み込む。ファイルが無い場合は既定値を作成して保存する。
        /// 壊れている場合は既定値を返し、元ファイルを .bak に退避する。
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    AppSettings created = AppSettings.CreateDefault();
                    Save(created);
                    return created;
                }

                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is null)
                {
                    return AppSettings.CreateDefault();
                }

                loaded.Items ??= [];
                return loaded;
            }
            catch (Exception)
            {
                TryBackupBrokenFile();
                return AppSettings.CreateDefault();
            }
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
