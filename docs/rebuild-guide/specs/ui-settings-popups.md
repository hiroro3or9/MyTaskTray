# 設定画面のPopup仕様

単位はDIP。箇条書きのインデントは親子構造。属性未指定はWPF既定/親からの継承。Bindingは同名の表示状態・編集値へ接続する。イベント名は役割を識別するラベルで、段階別の動作仕様へ結び付ける。DynamicResourceはテーマ色、StaticResourceは共通スタイル。これは画面を再構成するための設計表であり、元XAMLファイルの添付ではない。


## 
HotKeyPopup

- **Popup** — x:Name=HotKeyPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Top ; PlacementTarget={Binding ElementName=HotKeyButton} ; VerticalOffset=-4 ; HorizontalOffset=-10 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; Width=420 ; Margin=10
    - **StackPanel** — Margin=14
      - **TextBlock** — Text=メニューを表示するホットキー ; FontWeight=SemiBold
      - **TextBlock** — Margin=0,4,0,10 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=Ctrl+Alt+V のように入力します。空欄なら無効です。通常の文字入力を妨げないよう、Ctrl / Alt / Win のいずれかを必ず含めてください。ただし文字を入力しないキー（無変換・変換・アプリケーション・Pause・F13〜F24）は単体でも指定できます。この欄でそのキーを押すと入ります（登録しているあいだ、そのキー本来の働きは使えなくなります）。
      - **TextBox** — x:Name=HotKeyBox ; InputMethod.IsInputMethodEnabled=False ; ContextMenu={x:Null} ; PreviewKeyDown=OnHotKeyBoxPreviewKeyDown ; Text={Binding MenuHotKey, UpdateSourceTrigger=PropertyChanged} ; ToolTip=キーは A〜Z、0〜9、F1〜F24、無変換、変換、アプリケーション、Pause を指定できます
      - **TextBlock** — Margin=0,8,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text={Binding MenuHotKeyStatus}
      - **Border** — Height=1 ; Margin=0,14,0,12 ; Background={DynamicResource Brush.Separator}
      - **TextBlock** — Text=Swap コピーのホットキー ; FontWeight=SemiBold
      - **TextBlock** — Margin=0,4,0,10 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=選択している文字をコピーし、その場へ元のクリップボードの内容を貼り付けます。次に貼り付けられるのは、選択していた文字です。空欄なら無効です。書き方はメニュー用と同じで、メニュー用と同じキーは指定できません。
      - **TextBox** — x:Name=SwapCopyHotKeyBox ; InputMethod.IsInputMethodEnabled=False ; ContextMenu={x:Null} ; PreviewKeyDown=OnSwapCopyHotKeyBoxPreviewKeyDown ; Text={Binding SwapCopyHotKey, UpdateSourceTrigger=PropertyChanged} ; ToolTip=キーは A〜Z、0〜9、F1〜F24、無変換、変換、アプリケーション、Pause を指定できます
      - **TextBlock** — Margin=0,8,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text={Binding SwapCopyHotKeyStatus}

## 
ActionSettingsPopup

- **Popup** — x:Name=ActionSettingsPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Top ; PlacementTarget={Binding ElementName=ActionSettingsButton} ; VerticalOffset=-4 ; HorizontalOffset=-10 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; Width=470 ; Margin=10
    - **StackPanel** — Margin=14
      - **TextBlock** — Text=アクションの表示 ; FontWeight=SemiBold
      - **TextBlock** — Margin=0,4,0,10 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=チェックした機能を、下に示すメニューへ表示します。実行中の作業は、完了またはキャンセルまで先頭に表示されます。
      - **ScrollViewer** — MaxHeight=320 ; VerticalScrollBarVisibility=Auto
        - **StackPanel**
          - **StackPanel** — Visibility={Binding HasWorkToolActionSettings, Converter={StaticResource BoolToVis}}
            - **TextBlock** — Text=作業ツール ; FontSize=14 ; FontWeight=SemiBold
            - **TextBlock** — Margin=0,2,0,3 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=通常時に、メニュー下部の「作業ツール」へ表示されます。
            - **ItemsControl** — ItemsSource={Binding WorkToolActionSettings} ; ItemTemplate={StaticResource ActionSettingTemplate}
          - **Border** — Height=1 ; Margin=0,10,0,10 ; Background={DynamicResource Brush.Separator}
          - **StackPanel** — Visibility={Binding HasContextualActionSettings, Converter={StaticResource BoolToVis}}
            - **TextBlock** — Text=この内容でできること ; FontSize=14 ; FontWeight=SemiBold
            - **TextBlock** — Margin=0,2,0,3 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=クリップボードが条件に合うときだけ、メニュー上部へ表示されます。
            - **ItemsControl** — ItemsSource={Binding ContextualActionSettings} ; ItemTemplate={StaticResource ActionSettingTemplate}

## 
SequentialCapturePopup

- **Popup** — x:Name=SequentialCapturePopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Top ; PlacementTarget={Binding ElementName=SequentialCaptureButton} ; VerticalOffset=-4 ; HorizontalOffset=-10 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; Width=470 ; Margin=10
    - **StackPanel** — Margin=14
      - **TextBlock** — Text=連続コピーで集める操作 ; FontWeight=SemiBold
      - **TextBlock** — Margin=0,4,0,10 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=Windows はクリップボードが変わったことしか伝えないため、どの操作の結果かはこのアプリが推測します。どこまでを 1 件として集めるかを選べます。
      - **ComboBox** — x:Name=SequentialCaptureBox ; ItemsSource={Binding SequentialCaptureOptions} ; DisplayMemberPath=Name ; SelectedValuePath=Trigger ; SelectedValue={Binding SequentialCaptureTrigger, Mode=TwoWay}
      - **TextBlock** — Margin=0,8,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text={Binding SequentialCaptureStatus}
      - **TextBlock** — Margin=0,10,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=パスワード管理ソフトなど、コピー元が他のアプリでの保存を断っている内容は、どの設定でも集めません。

## 
SprintPopup

- **Popup** — x:Name=SprintPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Top ; PlacementTarget={Binding ElementName=SprintButton} ; VerticalOffset=-4 ; HorizontalOffset=-10
  - **Border** — Style={StaticResource PopupCard} ; Width=420 ; Margin=10
    - **StackPanel** — Margin=14
      - **TextBlock** — Text=スプリントの区切り ; FontWeight=SemiBold
      - **TextBlock** — Margin=0,4,0,10 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text=どれか 1 つのスプリントの開始日と長さを決めると、{date@sprint} などが「今日を含むスプリントの開始日」を指すようになります。
      - **Grid**
        - **Grid.ColumnDefinitions**
          - **ColumnDefinition** — Width=Auto
          - **ColumnDefinition** — Width=*
          - **ColumnDefinition** — Width=Auto
          - **ColumnDefinition** — Width=Auto
          - **ColumnDefinition** — Width=Auto
        - **TextBlock** — Grid.Column=0 ; Text=基準日 ; VerticalAlignment=Center ; Margin=0,0,8,0
        - **TextBox** — Grid.Column=1 ; x:Name=SprintAnchorBox ; MinWidth=110 ; Text={Binding SprintAnchorText, UpdateSourceTrigger=PropertyChanged} ; ToolTip=yyyy-MM-dd 形式（例 2026-04-06）。空にするとスプリントの差し込みを使いません
        - **TextBlock** — Grid.Column=2 ; Text=長さ ; VerticalAlignment=Center ; Margin=14,0,8,0
        - **TextBox** — Grid.Column=3 ; Width=52 ; TextAlignment=Right ; Text={Binding SprintLengthText, UpdateSourceTrigger=PropertyChanged} ; ToolTip=スプリント 1 つの日数（2 週間なら 14）
        - **TextBlock** — Grid.Column=4 ; Text=日 ; VerticalAlignment=Center ; Margin=6,0,0,0
      - **TextBlock** — Margin=0,10,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap ; Text={Binding SprintStatus}

## 
InsertPopup

- **Popup** — x:Name=InsertPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Bottom ; PlacementTarget={Binding ElementName=InsertButton} ; VerticalOffset=4 ; HorizontalOffset=-260 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; Width=470 ; Margin=10
    - **DockPanel** — Margin=10
      - **Grid** — DockPanel.Dock=Top ; Margin=0,0,0,8
        - **TextBox** — x:Name=PlaceholderFilterBox ; TextChanged=OnPlaceholderFilterChanged
        - **TextBlock** — x:Name=PlaceholderFilterHint ; Text=差し込みを検索（日付、計算、連番 など） ; Margin=10,0,0,0 ; VerticalAlignment=Center ; IsHitTestVisible=False ; Foreground={DynamicResource Brush.Text.Disabled}
      - **TextBlock** — DockPanel.Dock=Bottom ; Margin=2,8,0,0 ; Style={StaticResource Hint} ; Text=クリックするとカーソル位置に挿入されます。右側は現在の値です。
      - **ScrollViewer** — MaxHeight=330 ; VerticalScrollBarVisibility=Auto
        - **ItemsControl** — x:Name=PlaceholderList
          - **ItemsControl.GroupStyle**
            - **GroupStyle**
              - **GroupStyle.HeaderTemplate**
                - **DataTemplate**
                  - **TextBlock** — Text={Binding Name} ; FontSize=11 ; FontWeight=SemiBold ; Margin=4,8,0,3 ; Foreground={DynamicResource Brush.Text.Secondary}
          - **ItemsControl.ItemTemplate**
            - **DataTemplate**
              - **Button** — Style={StaticResource RowButton} ; Click=OnInsertPlaceholder ; ToolTip={Binding Description}
                - **Grid**
                  - **Grid.ColumnDefinitions**
                    - **ColumnDefinition** — Width=185
                    - **ColumnDefinition** — Width=*
                    - **ColumnDefinition** — Width=Auto
                  - **TextBlock** — Grid.Column=0 ; Text={Binding Token} ; FontFamily=Consolas, Yu Gothic UI ; Foreground={DynamicResource Brush.Accent} ; TextTrimming=CharacterEllipsis
                  - **TextBlock** — Grid.Column=1 ; Text={Binding Description} ; Margin=8,0,8,0 ; FontSize=11.5 ; Foreground={DynamicResource Brush.Text.Secondary} ; TextTrimming=CharacterEllipsis
                  - **TextBlock** — Grid.Column=2 ; Text={Binding Sample} ; MaxWidth=120 ; FontSize=11.5 ; TextAlignment=Right ; TextTrimming=CharacterEllipsis

## 
CategoryPopup

- **Popup** — x:Name=CategoryPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Bottom ; PlacementTarget={Binding ElementName=CategoryBox} ; VerticalOffset=4 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; MinWidth=180 ; Margin=10
    - **DockPanel** — Margin=8
      - **Button** — DockPanel.Dock=Top ; Style={StaticResource RowButton} ; Content=トップレベル ; FontWeight=SemiBold ; HorizontalContentAlignment=Left ; Click=OnClearCategory
      - **Border** — DockPanel.Dock=Top ; Height=1 ; Margin=4,5 ; Background={DynamicResource Brush.Separator}
      - **ScrollViewer** — MaxHeight=260 ; VerticalScrollBarVisibility=Auto
        - **ItemsControl** — ItemsSource={Binding KnownCategories}
          - **ItemsControl.ItemTemplate**
            - **DataTemplate**
              - **Button** — Style={StaticResource RowButton} ; Click=OnPickCategory ; HorizontalContentAlignment=Left
                - **Grid** — Width=220
                  - **Grid.ColumnDefinitions**
                    - **ColumnDefinition** — Width=18
                    - **ColumnDefinition** — Width=22
                    - **ColumnDefinition** — Width=*
                  - **Ellipse** — Grid.Column=0 ; Width=9 ; Height=9 ; Fill={Binding ColorBrush} ; Stroke={DynamicResource Brush.Border.Strong} ; StrokeThickness=0.7 ; Visibility={Binding HasColor, Converter={StaticResource BoolToVis}}
                  - **TextBlock** — Grid.Column=1 ; Text={Binding IconGlyph} ; FontFamily={Binding IconFontFamily} ; FontSize=13 ; Foreground={Binding IconBrush} ; HorizontalAlignment=Center ; VerticalAlignment=Center ; Visibility={Binding HasIcon, Converter={StaticResource BoolToVis}}
                  - **TextBlock** — Grid.Column=2 ; Text={Binding Name} ; Margin=4,0,0,0 ; VerticalAlignment=Center

## 
AppPopup

- **Popup** — x:Name=AppPopup ; StaysOpen=False ; AllowsTransparency=True ; Focusable=True ; Placement=Bottom ; PlacementTarget={Binding ElementName=AppProcessBox} ; VerticalOffset=4 ; PreviewKeyDown=OnPopupPreviewKeyDown
  - **Border** — Style={StaticResource PopupCard} ; MinWidth=220 ; Margin=10
    - **StackPanel** — Margin=8
      - **TextBlock** — Margin=4,2,4,6 ; FontSize=11 ; TextWrapping=Wrap ; Foreground={DynamicResource Brush.Text.Secondary} ; Text=トレイメニューを開いたときに前面だったアプリです。
      - **ItemsControl** — ItemsSource={Binding KnownApps}
        - **ItemsControl.ItemTemplate**
          - **DataTemplate**
            - **Button** — Style={StaticResource RowButton} ; Click=OnPickApp ; Content={Binding} ; HorizontalContentAlignment=Left
