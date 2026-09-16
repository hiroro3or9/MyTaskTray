# 設定画面の行テンプレート・局所スタイル

単位はDIP。箇条書きのインデントは親子構造。属性未指定はWPF既定/親からの継承。Bindingは同名の表示状態・編集値へ接続する。イベント名は役割を識別するラベルで、段階別の動作仕様へ結び付ける。DynamicResourceはテーマ色、StaticResourceは共通スタイル。これは画面を再構成するための設計表であり、元XAMLファイルの添付ではない。


## 
BooleanToVisibilityConverter
 
BoolToVis

- **BooleanToVisibilityConverter** — x:Key=BoolToVis

## 
NotBoolToVisibilityConverter
 
NotBoolToVis

- **NotBoolToVisibilityConverter** — x:Key=NotBoolToVis

## 
Style
 
CategoryPalette

- **Style** — x:Key=CategoryPalette ; TargetType=ListBox
  - **Setter** — Property=Background ; Value=Transparent
  - **Setter** — Property=Foreground ; Value={DynamicResource Brush.Text}
  - **Setter** — Property=BorderThickness ; Value=0
  - **Setter** — Property=Padding ; Value=0
  - **Setter** — Property=ScrollViewer.HorizontalScrollBarVisibility ; Value=Disabled
  - **Setter** — Property=ScrollViewer.VerticalScrollBarVisibility ; Value=Disabled
  - **Setter** — Property=ItemsPanel
    - **Setter.Value**
      - **ItemsPanelTemplate**
        - **WrapPanel**

## 
Style
 
CategoryPaletteItem

- **Style** — x:Key=CategoryPaletteItem ; TargetType=ListBoxItem
  - **Setter** — Property=Margin ; Value=0,0,5,5
  - **Setter** — Property=Padding ; Value=0
  - **Setter** — Property=FocusVisualStyle ; Value={x:Null}
  - **Setter** — Property=Template
    - **Setter.Value**
      - **ControlTemplate** — TargetType=ListBoxItem
        - **Border** — x:Name=Tile ; CornerRadius=5 ; BorderThickness=1 ; BorderBrush={DynamicResource Brush.Border} ; Background=Transparent
          - **ContentPresenter**
        - **ControlTemplate.Triggers**
          - **Trigger** — Property=IsMouseOver ; Value=True
            - **Setter** — TargetName=Tile ; Property=Background ; Value={DynamicResource Brush.Hover}
          - **Trigger** — Property=IsSelected ; Value=True
            - **Setter** — TargetName=Tile ; Property=BorderBrush ; Value={DynamicResource Brush.Accent}
            - **Setter** — TargetName=Tile ; Property=BorderThickness ; Value=2
            - **Setter** — TargetName=Tile ; Property=Background ; Value={DynamicResource Brush.Selected}

## 
DataTemplate
 
ClipItemTemplate

- **DataTemplate** — x:Key=ClipItemTemplate
  - **Grid**
    - **Grid** — Visibility={Binding IsNotSeparator, Converter={StaticResource BoolToVis}}
      - **Grid.ColumnDefinitions**
        - **ColumnDefinition** — Width=*
        - **ColumnDefinition** — Width=Auto
      - **StackPanel** — Grid.Column=0
        - **TextBlock** — Text={Binding DisplayLabel} ; FontWeight=SemiBold ; TextTrimming=CharacterEllipsis
        - **TextBlock** — Text={Binding TextPreview} ; Margin=0,2,0,0 ; FontSize=11.5 ; Foreground={DynamicResource Brush.Text.Secondary} ; TextTrimming=CharacterEllipsis
      - **StackPanel** — Grid.Column=1 ; Orientation=Horizontal ; VerticalAlignment=Top ; Margin=4,1,0,0
        - **Border** — Style={StaticResource Badge} ; ToolTip=書式付きでコピーします ; Visibility={Binding HasFormat, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text={Binding FormatLabel} ; Style={StaticResource BadgeText}
        - **Border** — Style={StaticResource SeqBadge} ; Visibility={Binding UsesSequence, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text=連番 ; Style={StaticResource SeqBadgeText}
        - **Border** — Style={StaticResource Badge} ; Visibility={Binding UsesInputs, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text=入力 ; Style={StaticResource BadgeText}
        - **Border** — Style={StaticResource Badge} ; Visibility={Binding HasSmartCondition, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text=スマート ; Style={StaticResource BadgeText}
        - **Border** — Style={StaticResource Badge} ; ToolTip=クリップボードの各行に適用します ; Visibility={Binding UsesEachLine, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text=複数行 ; Style={StaticResource BadgeText}
        - **Border** — Style={StaticResource Badge} ; ToolTip=前面のアプリによって表示が変わります ; Visibility={Binding HasAppCondition, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text=アプリ ; Style={StaticResource BadgeText}
        - **Border** — Style={StaticResource Badge} ; Visibility={Binding HasCategory, Converter={StaticResource BoolToVis}}
          - **TextBlock** — Text={Binding Category} ; Style={StaticResource BadgeText} ; MaxWidth=90 ; TextTrimming=CharacterEllipsis
    - **Grid** — Height=20 ; Visibility={Binding IsSeparator, Converter={StaticResource BoolToVis}}
      - **Border** — Height=1 ; VerticalAlignment=Center ; Background={DynamicResource Brush.Separator}
      - **TextBlock** — Text=区切り線 ; FontSize=10.5 ; Padding=8,0 ; HorizontalAlignment=Center ; VerticalAlignment=Center ; Background={DynamicResource Brush.Surface} ; Foreground={DynamicResource Brush.Text.Secondary}

## 
DataTemplate
 
MenuOutlineRowTemplate

- **DataTemplate** — x:Key=MenuOutlineRowTemplate
  - **Grid**
    - **Border** — Padding=4,7,4,5 ; Margin=0,2,0,1 ; BorderBrush={DynamicResource Brush.Border} ; BorderThickness=0,0,0,1 ; Visibility={Binding IsSection, Converter={StaticResource BoolToVis}}
      - **Grid**
        - **Grid.ColumnDefinitions**
          - **ColumnDefinition** — Width=*
          - **ColumnDefinition** — Width=Auto
        - **TextBlock** — Text={Binding SectionTitle} ; FontWeight=SemiBold
        - **TextBlock** — Grid.Column=1 ; Text={Binding SectionHint} ; FontSize=10.5 ; Foreground={DynamicResource Brush.Text.Secondary}
    - **Grid** — Margin=2,2,0,2 ; Visibility={Binding IsCategory, Converter={StaticResource BoolToVis}}
      - **Grid.ColumnDefinitions**
        - **ColumnDefinition** — Width=4
        - **ColumnDefinition** — Width=24
        - **ColumnDefinition** — Width=Auto
        - **ColumnDefinition** — Width=*
        - **ColumnDefinition** — Width=Auto
      - **Border** — Grid.Column=0 ; Width=3 ; Height=22 ; CornerRadius=2 ; HorizontalAlignment=Left ; Background={Binding CategoryColorBrush}
      - **Button** — Grid.Column=1 ; Content={Binding ExpandGlyph} ; Width=22 ; Height=22 ; MinHeight=0 ; Padding=0 ; Style={StaticResource SubtleButton} ; ToolTip=カテゴリを折りたたむ / 展開する ; Click=OnToggleCategory
      - **Grid** — Grid.Column=2 ; Width=22 ; Height=22 ; Margin=3,0,1,0 ; Visibility={Binding HasCategoryIcon, Converter={StaticResource BoolToVis}}
        - **TextBlock** — Text={Binding CategoryIconGlyph} ; FontFamily={Binding CategoryIconFontFamily} ; FontSize=14 ; Foreground={Binding CategoryIconBrush} ; HorizontalAlignment=Center ; VerticalAlignment=Center
      - **TextBlock** — Grid.Column=3 ; Text={Binding Category} ; Margin=5,0,8,0 ; VerticalAlignment=Center ; FontWeight=SemiBold ; TextTrimming=CharacterEllipsis
      - **Border** — Grid.Column=4 ; Style={StaticResource Badge} ; VerticalAlignment=Center
        - **TextBlock** — Text={Binding ItemCountText} ; Style={StaticResource BadgeText}
    - **Border** — Visibility={Binding IsItem, Converter={StaticResource BoolToVis}}
      - **Border.Style**
        - **Style** — TargetType=Border
          - **Setter** — Property=Margin ; Value=0
          - **Style.Triggers**
            - **DataTrigger** — Binding={Binding IsCategoryItem} ; Value=True
              - **Setter** — Property=Margin ; Value=27,0,0,0
      - **ContentPresenter** — Content={Binding Item} ; ContentTemplate={StaticResource ClipItemTemplate}

## 
DataTemplate
 
ActionSettingTemplate

- **DataTemplate** — x:Key=ActionSettingTemplate
  - **CheckBox** — IsChecked={Binding IsVisible} ; Margin=2,5 ; HorizontalContentAlignment=Stretch
    - **Grid** — Width=400
      - **Grid.ColumnDefinitions**
        - **ColumnDefinition** — Width=*
        - **ColumnDefinition** — Width=Auto
      - **StackPanel** — Grid.Column=0 ; Margin=0,0,12,0
        - **TextBlock** — Text={Binding Name} ; FontWeight=SemiBold
        - **TextBlock** — Text={Binding Description} ; Margin=0,2,0,0 ; Style={StaticResource Hint} ; TextWrapping=Wrap
      - **TextBlock** — Grid.Column=1 ; Text={Binding Group} ; VerticalAlignment=Top ; Margin=6,1,0,0 ; Foreground={DynamicResource Brush.Text.Secondary}
