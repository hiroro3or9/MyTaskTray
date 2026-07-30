using System.ComponentModel;
using MyTaskTray.Services;

namespace MyTaskTray.ViewModels
{
    /// <summary>
    /// 「差し込みを挿入」パネルの 1 行。<see cref="Sample"/> には現在の展開結果を入れる。
    /// </summary>
    public sealed class PlaceholderRow : INotifyPropertyChanged
    {
        private string _sample = string.Empty;

        public PlaceholderRow(PlaceholderInfo info)
        {
            Info = info;
        }

        public PlaceholderInfo Info { get; }

        public string Token => Info.Token;

        public string Group => Info.Group;

        public string Description => Info.Description;

        /// <summary>いま挿入した場合に得られる文字列。</summary>
        public string Sample
        {
            get => _sample;
            set
            {
                if (string.Equals(_sample, value, StringComparison.Ordinal))
                {
                    return;
                }

                _sample = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sample)));
            }
        }

        /// <summary>絞り込み検索に一致するかどうか。</summary>
        public bool Matches(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return Token.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || Description.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                || Group.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
