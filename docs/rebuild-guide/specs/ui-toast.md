# ToastWindow の画面仕様

単位はDIP。箇条書きのインデントは親子構造。属性未指定はWPF既定/親からの継承。Bindingは同名の表示状態・編集値へ接続する。イベント名は役割を識別するラベルで、段階別の動作仕様へ結び付ける。DynamicResourceはテーマ色、StaticResourceは共通スタイル。これは画面を再構成するための設計表であり、元XAMLファイルの添付ではない。

- **Window** — x:Class=MyTaskTray.ToastWindow ; WindowStyle=None ; AllowsTransparency=True ; Background=Transparent ; ShowInTaskbar=False ; Topmost=True ; ResizeMode=NoResize ; SizeToContent=WidthAndHeight ; ShowActivated=False ; FontFamily=Yu Gothic UI ; FontSize=12.5 ; Foreground={DynamicResource Brush.Text} ; Opacity=0 ; MouseLeftButtonDown=OnClicked
  - **Border** — Margin=14 ; Padding=14,11 ; CornerRadius=8 ; MaxWidth=380 ; Background={DynamicResource Brush.Toast.Bg} ; BorderBrush={DynamicResource Brush.Toast.Border} ; BorderThickness=1
    - **Border.Effect**
      - **DropShadowEffect** — BlurRadius=18 ; ShadowDepth=3 ; Opacity=0.3 ; Color=Black
    - **StackPanel** — Orientation=Horizontal
      - **Border** — Width=3 ; CornerRadius=2 ; Margin=0,1,11,1 ; Background={DynamicResource Brush.Accent}
      - **StackPanel**
        - **TextBlock** — x:Name=TitleText ; FontWeight=SemiBold
        - **TextBlock** — x:Name=BodyText ; Margin=0,3,0,0 ; MaxWidth=320 ; TextTrimming=CharacterEllipsis ; Foreground={DynamicResource Brush.Text.Secondary}
