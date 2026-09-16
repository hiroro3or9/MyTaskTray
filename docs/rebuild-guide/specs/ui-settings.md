# 設定画面: 外枠・一覧・フッター

単位DIP。右ペインGridへui-settings-category.mdとui-settings-item.mdのルートを追加する。Popupはui-settings-popups.md、ローカルスタイルはui-settings-templates.md。

## ウィンドウ属性

x:Class=MyTaskTray.SettingsWindow ; Title=MyTaskTray の設定 ; Height=800 ; Width=980 ; MinHeight=560 ; MinWidth=840 ; WindowStartupLocation=CenterScreen ; FontFamily=Yu Gothic UI ; FontSize=13 ; Background={DynamicResource Brush.Window.Bg} ; Foreground={DynamicResource Brush.Text} ; PreviewKeyDown=OnWindowPreviewKeyDown ; Closing=OnWindowClosing

## 本体

- **Grid**
  - **Grid.RowDefinitions**
    - **RowDefinition** — Height=Auto
    - **RowDefinition** — Height=*
    - **RowDefinition** — Height=Auto
  - **StackPanel** — Grid.Row=0 ; Margin=18,16,18,0
    - **TextBlock** — Text=メニュー構成 ; FontSize=18 ; FontWeight=SemiBold
    - **TextBlock** — Margin=0,3,0,0 ; Style={StaticResource Hint} ; Text=カテゴリと項目を、トレイメニューに表示される構造で編集します。ドラッグして移動・並べ替えできます。
  - **Grid** — Grid.Row=1 ; Margin=18,14,18,0
    - **Grid.ColumnDefinitions**
      - **ColumnDefinition** — x:Name=ListColumn ; Width=330 ; MinWidth=250
      - **ColumnDefinition** — Width=10
      - **ColumnDefinition** — Width=* ; MinWidth=400
    - **Border** — Grid.Column=0 ; Style={StaticResource Card}
      - **DockPanel** — Margin=10
        - **Grid** — DockPanel.Dock=Top ; Margin=0,0,0,8
          - **TextBox** — x:Name=FilterBox ; Padding=8,5,28,5 ; Text={Binding FilterText, UpdateSourceTrigger=PropertyChanged}
          - **TextBlock** — Text=項目を検索（Ctrl+F） ; Margin=10,0,0,0 ; VerticalAlignment=Center ; IsHitTestVisible=False ; Foreground={DynamicResource Brush.Text.Disabled} ; Visibility={Binding HasFilter, Converter={StaticResource NotBoolToVis}}
          - **Button** — Content=✕ ; Width=22 ; Height=22 ; MinHeight=0 ; Padding=0 ; FontSize=10 ; HorizontalAlignment=Right ; Margin=0,0,4,0 ; Style={StaticResource SubtleButton} ; ToolTip=検索をクリア ; Click=OnClearFilter ; Visibility={Binding HasFilter, Converter={StaticResource BoolToVis}}
        - **Grid** — DockPanel.Dock=Bottom ; Margin=0,8,0,0
          - **StackPanel** — Orientation=Horizontal
            - **Button** — Content=追加 ; Padding=10,4 ; Click=OnAddItem ; ToolTip=新しい項目を追加（Ctrl+N）
            - **Button** — Content=区切り線 ; Padding=10,4 ; Margin=5,0,0,0 ; Click=OnAddSeparator ; ToolTip=メニューに水平線を入れる
            - **Button** — Content=複製 ; Padding=10,4 ; Margin=5,0,0,0 ; Click=OnDuplicateItem ; IsEnabled={Binding CanDuplicate} ; ToolTip=選択項目を複製（Ctrl+D）
            - **Button** — Content=削除 ; Padding=10,4 ; Margin=5,0,0,0 ; Click=OnDeleteItem ; IsEnabled={Binding CanDelete} ; ToolTip=選択した項目またはカテゴリを削除（Delete）
          - **StackPanel** — Orientation=Horizontal ; HorizontalAlignment=Right
            - **Button** — Content=▲ ; Style={StaticResource IconButton} ; Click=OnMoveUp ; IsEnabled={Binding CanReorder} ; ToolTip=上へ移動（Alt+↑）
            - **Button** — Content=▼ ; Style={StaticResource IconButton} ; Margin=4,0,0,0 ; Click=OnMoveDown ; IsEnabled={Binding CanReorder} ; ToolTip=下へ移動（Alt+↓）
        - **TextBlock** — DockPanel.Dock=Bottom ; Margin=2,8,0,0 ; Style={StaticResource Hint} ; Text={Binding StatusText}
        - **ListBox** — x:Name=ItemsList ; ItemsSource={Binding MenuOutline} ; SelectedItem={Binding SelectedOutlineRow, Mode=TwoWay} ; ItemTemplate={StaticResource MenuOutlineRowTemplate} ; AllowDrop=True ; SelectionChanged=OnSelectionChanged ; PreviewMouseLeftButtonDown=OnListPreviewMouseLeftButtonDown ; PreviewMouseMove=OnListPreviewMouseMove ; DragOver=OnListDragOver ; DragLeave=OnListDragLeave ; Drop=OnListDrop ; PreviewKeyDown=OnListPreviewKeyDown
    - **GridSplitter** — Grid.Column=1 ; Width=10 ; HorizontalAlignment=Stretch ; VerticalAlignment=Stretch
    - **Border** — Grid.Column=2 ; Style={StaticResource Card}
      - **Grid**
        - **TextBlock** — Text={Binding EditorHint} ; MaxWidth=320 ; Margin=24 ; HorizontalAlignment=Center ; VerticalAlignment=Center ; TextAlignment=Center ; TextWrapping=Wrap ; Foreground={DynamicResource Brush.Text.Secondary} ; Visibility={Binding ShowEditorHint, Converter={StaticResource BoolToVis}}
  - **Grid** — Grid.Row=2 ; Margin=18,14,18,16
    - **Grid.ColumnDefinitions**
      - **ColumnDefinition** — Width=*
      - **ColumnDefinition** — Width=Auto
    - **WrapPanel** — Grid.Column=0 ; VerticalAlignment=Center ; Margin=0,0,12,0
      - **CheckBox** — Content=コピーしたときに通知を表示する ; VerticalAlignment=Center ; IsChecked={Binding ShowCopyNotification}
      - **Button** — x:Name=HotKeyButton ; Content=ホットキーの設定 ; Margin=16,0,0,0 ; Style={StaticResource SubtleButton} ; Click=OnOpenHotKeySettings ; ToolTip=トレイメニューの表示と Swap コピーのグローバルホットキーを設定します
      - **Button** — x:Name=ActionSettingsButton ; Content=アクションの表示 ; Margin=8,0,0,0 ; Style={StaticResource SubtleButton} ; Click=OnOpenActionSettings ; IsEnabled={Binding HasActionSettings} ; ToolTip=組み込みアクションの表示と、表示されるメニューを確認します
      - **Button** — x:Name=SequentialCaptureButton ; Content=連続コピーの設定 ; Margin=8,0,0,0 ; Style={StaticResource SubtleButton} ; Click=OnOpenSequentialCaptureSettings ; ToolTip=連続コピーが 1 件として集める操作の範囲を決めます
      - **Button** — x:Name=SprintButton ; Content=スプリントの設定 ; Margin=8,0,0,0 ; Style={StaticResource SubtleButton} ; Click=OnOpenSprintSettings ; ToolTip={}{date@sprint} などが参照する基準日と長さを決めます
      - **Button** — x:Name=FolderButton ; Content=設定フォルダーを開く ; Margin=8,0,0,0 ; Style={StaticResource SubtleButton} ; Click=OnOpenFolder
    - **StackPanel** — Grid.Column=1 ; Orientation=Horizontal ; HorizontalAlignment=Right
      - **Button** — Content=キャンセル ; Width=104 ; Click=OnCancel ; IsCancel=True
      - **Button** — Content=保存して閉じる ; Width=132 ; Margin=8,0,0,0 ; Style={StaticResource AccentButton} ; Click=OnSave ; IsDefault=True
