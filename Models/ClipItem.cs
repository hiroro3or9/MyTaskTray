using System.ComponentModel;
using System.Text.Json.Serialization;
using MyTaskTray.Services;

namespace MyTaskTray.Models
{
    /// <summary>
    /// メニューに表示する 1 項目。区切り線もこのクラスで表現する。
    /// </summary>
    public class ClipItem : INotifyPropertyChanged
    {
        /// <summary>連番を新規作成またはリセットしたときの値。</summary>
        public const int InitialSequenceValue = 1;

        private string _name = string.Empty;
        private string _text = string.Empty;
        private string _category = string.Empty;
        private bool _isSeparator;
        private int _sequenceValue = InitialSequenceValue;
        private int _sequenceStep = 1;
        private ClipboardMatchKind _clipboardCondition;
        private string _clipboardPattern = string.Empty;
        private string _appProcess = string.Empty;
        private string _appTitlePattern = string.Empty;

        /// <summary>
        /// 項目を識別する ID。連番カウンターの引き継ぎに使う。
        /// 既定値を空にしているのは「設定ファイルに書かれていなかった」ことを見分けるため。
        /// ここで <c>Guid.NewGuid()</c> を初期値にすると、手で書いた ID なしの項目が
        /// 読み込むたびに別の ID になり、引き継ぎの突き合わせが必ず失敗する。
        /// 空の場合は <see cref="Services.SettingsStore.Load"/> と設定画面の保存時に採番する。
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>新しい項目の ID を作る。</summary>
        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>メニューに表示する名前。</summary>
        public string Name
        {
            get => _name;
            set => Set(ref _name, value ?? string.Empty, nameof(Name), nameof(DisplayName), nameof(DisplayLabel));
        }

        /// <summary>
        /// クリップボードにコピーする文字列。
        /// <c>{date}</c> などの差し込みを含められる（<see cref="TemplateEngine"/> で展開）。
        /// </summary>
        public string Text
        {
            get => _text;
            set => Set(
                ref _text,
                value ?? string.Empty,
                nameof(Text),
                nameof(DisplayName),
                nameof(DisplayLabel),
                nameof(TextPreview),
                nameof(UsesSequence),
                nameof(UsesInputs));
        }

        /// <summary>
        /// クリップボードの内容に応じて項目を表示する条件。
        /// <see cref="ClipboardMatchKind.Always"/> なら従来どおり常に表示する。
        /// </summary>
        public ClipboardMatchKind ClipboardCondition
        {
            get => _clipboardCondition;
            set => Set(
                ref _clipboardCondition,
                value,
                nameof(ClipboardCondition),
                nameof(HasSmartCondition),
                nameof(IsRegexCondition));
        }

        /// <summary>
        /// <see cref="ClipboardCondition"/> が <see cref="ClipboardMatchKind.Regex"/> のときに使う正規表現。
        /// 名前付きまたは番号付きキャプチャは <c>{match:名前}</c> で出力に差し込める。
        /// </summary>
        public string ClipboardPattern
        {
            get => _clipboardPattern;
            set => Set(ref _clipboardPattern, value ?? string.Empty, nameof(ClipboardPattern));
        }

        /// <summary>
        /// この項目を表示する前面アプリの実行ファイル名。カンマ区切りで複数書ける。
        /// 空ならアプリを問わない。<c>.exe</c> は省略でき、大文字小文字は区別しない。
        /// </summary>
        public string AppProcess
        {
            get => _appProcess;
            set => Set(
                ref _appProcess,
                value ?? string.Empty,
                nameof(AppProcess),
                nameof(HasAppCondition));
        }

        /// <summary>
        /// この項目を表示する前面ウィンドウのタイトルに対する正規表現。空ならタイトルを見ない。
        /// </summary>
        public string AppTitlePattern
        {
            get => _appTitlePattern;
            set => Set(
                ref _appTitlePattern,
                value ?? string.Empty,
                nameof(AppTitlePattern),
                nameof(HasAppCondition));
        }

        /// <summary>
        /// 所属カテゴリ。空文字ならトップレベルに表示、
        /// 値があればその名前のサブメニュー配下に表示する。
        /// </summary>
        public string Category
        {
            get => _category;
            set => Set(
                ref _category,
                value ?? string.Empty,
                nameof(Category),
                nameof(DisplayName),
                nameof(HasCategory));
        }

        /// <summary>true の場合、メニュー上では区切り線として描画する。</summary>
        public bool IsSeparator
        {
            get => _isSeparator;
            set => Set(
                ref _isSeparator,
                value,
                nameof(IsSeparator),
                nameof(IsNotSeparator),
                nameof(DisplayName));
        }

        /// <summary><c>{seq}</c> が次に出力する番号。コピーするたびに <see cref="SequenceStep"/> 分進む。</summary>
        public int SequenceValue
        {
            get => _sequenceValue;
            set => Set(ref _sequenceValue, value, nameof(SequenceValue));
        }

        /// <summary>連番の増分。</summary>
        public int SequenceStep
        {
            get => _sequenceStep;
            set => Set(ref _sequenceStep, value, nameof(SequenceStep));
        }

        /// <summary>コピー文字列が連番の差し込みを含むかどうか。</summary>
        [JsonIgnore]
        public bool UsesSequence => TemplateEngine.ContainsSequence(Text);

        /// <summary><c>{input:名前}</c>（正規表現付きも含む）による複数回キャプチャを使うかどうか。</summary>
        [JsonIgnore]
        public bool UsesInputs => TemplateEngine.GetInputNames(Text).Count > 0;

        /// <summary>クリップボードに応じて表示されるスマートアクションかどうか。</summary>
        [JsonIgnore]
        public bool HasSmartCondition => ClipboardCondition != ClipboardMatchKind.Always;

        /// <summary>スマートアクションの条件として正規表現を使うかどうか。</summary>
        [JsonIgnore]
        public bool IsRegexCondition => ClipboardCondition == ClipboardMatchKind.Regex;

        /// <summary>前面アプリによる絞り込みが設定されているかどうか。</summary>
        [JsonIgnore]
        public bool HasAppCondition
            => !string.IsNullOrWhiteSpace(AppProcess) || !string.IsNullOrWhiteSpace(AppTitlePattern);

        /// <summary>区切り線ではない通常の項目かどうか（テンプレートの切り替えに使う）。</summary>
        [JsonIgnore]
        public bool IsNotSeparator => !IsSeparator;

        /// <summary>カテゴリが設定されているかどうか（バッジの表示に使う）。</summary>
        [JsonIgnore]
        public bool HasCategory => !string.IsNullOrWhiteSpace(Category);

        /// <summary>一覧の 1 行目に出す名前。</summary>
        [JsonIgnore]
        public string DisplayLabel
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }

                return string.IsNullOrWhiteSpace(Text)
                    ? "(名前なし)"
                    : TemplateEngine.ToSingleLine(Text, 40);
            }
        }

        /// <summary>一覧の 2 行目に出すコピー文字列のプレビュー。</summary>
        [JsonIgnore]
        public string TextPreview
            => string.IsNullOrEmpty(Text) ? "(空)" : TemplateEngine.ToSingleLine(Text, 60);

        /// <summary>設定画面の一覧に出す表示用テキスト。</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (IsSeparator)
                {
                    return "──────────  (区切り線)";
                }

                string label = string.IsNullOrWhiteSpace(Name) ? "(名前なし)" : Name;
                string prefix = string.IsNullOrWhiteSpace(Category) ? string.Empty : Category + " / ";
                return prefix + label;
            }
        }

        /// <summary>
        /// 連番を 1 回分進める。int の範囲を超える場合は負数などへ周回させず、
        /// 設定画面の「1 に戻す」と同じ初期値へ戻す。
        /// </summary>
        /// <returns>上限または下限を超えるため 1 に戻した場合は true。</returns>
        public bool AdvanceSequence()
        {
            int step = SequenceStep == 0 ? 1 : SequenceStep;
            long next = (long)SequenceValue + step;

            if (next is < int.MinValue or > int.MaxValue)
            {
                ResetSequence();
                return true;
            }

            SequenceValue = (int)next;
            return false;
        }

        /// <summary>連番を初期値へ戻す。</summary>
        public void ResetSequence() => SequenceValue = InitialSequenceValue;

        public ClipItem Clone() => new()
        {
            Id = Id,
            Name = Name,
            Text = Text,
            Category = Category,
            IsSeparator = IsSeparator,
            SequenceValue = SequenceValue,
            SequenceStep = SequenceStep,
            ClipboardCondition = ClipboardCondition,
            ClipboardPattern = ClipboardPattern,
            AppProcess = AppProcess,
            AppTitlePattern = AppTitlePattern,
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, params string[] names)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            foreach (string name in names)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
