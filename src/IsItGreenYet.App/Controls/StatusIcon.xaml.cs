using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.App.Controls;

/// <summary>
/// 清單列的狀態圖示（spec §7.1、§16.2、§16.4）。形狀與顏色依 <see cref="State"/> 決定，
/// 名稱與 tooltip 用狀態的中文（例如「失敗（逾時）」），不只靠顏色與形狀。
/// 只有「進行中」的圓弧會旋轉；Windows 關閉動畫效果（<see cref="SystemParameters.ClientAreaAnimation"/>）時改成靜態圓弧。
/// </summary>
public partial class StatusIcon : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(RunState),
        typeof(StatusIcon),
        new PropertyMetadata(null, OnStateChanged));

    /// <summary>設計選擇：一秒轉一圈，夠看得出在動，又不會像在閃。</summary>
    private static readonly Duration SpinPeriod = new(TimeSpan.FromSeconds(1));

    /// <summary>
    /// 不設上限時，只轉這一個圖示也會一直重畫，主視窗開著時約 13% 單核；這個上限約 4%，每幀轉 12 度，看起來一樣順。
    /// 數字是 2026-11-05 在 144 Hz 螢幕上量的，見驗收紀錄的 M38。
    /// </summary>
    private const int SpinFramesPerSecond = 30;

    private const double FullTurnDegrees = 360;

    // Style.Path.StatusIcon 以 Tag 切換顏色（見 Controls.xaml）。
    private const string SuccessTone = "Success";
    private const string FailureTone = "Failure";
    private const string WarningTone = "Warning";

    private const string AccentBrushKey = "Brush.Accent";
    private const string DangerBrushKey = "Brush.Danger";

    /// <summary>實心圓上的記號用深色（Accent Foreground），對比見 spec §16.2。</summary>
    private const string OnFillBrushKey = "Brush.Accent.Foreground";

    private bool _subscribed;

    public StatusIcon()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 視窗收到系統匣、列被捲出畫面時停止旋轉，不讓動畫時鐘在背景一直跑。
        IsVisibleChanged += (_, _) => UpdateSpin();
        Apply();
    }

    public RunState? State
    {
        get => (RunState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusIcon)d).Apply();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            // 靜態事件：只在畫面上時訂閱，離開畫面就取消，避免清單捲動時累積訂閱。
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _subscribed = true;
        }

        UpdateSpin();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            _subscribed = false;
        }

        Spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            Dispatcher.InvokeAsync(UpdateSpin);
        }
    }

    private void Apply()
    {
        var state = State;
        var display = state?.Display ?? DisplayState.Unknown;
        var tone = ToneOf(state);

        Ring.Tag = tone;
        Glyph.Tag = tone;
        Dot.Tag = tone;
        Ring.ClearValue(Shape.FillProperty);
        Glyph.ClearValue(Shape.StrokeProperty);

        Ring.Data = FindGeometry(display == DisplayState.InProgress ? "Geometry.Status.Arc" : "Geometry.Status.Circle");
        Glyph.Data = display switch
        {
            DisplayState.Success => FindGeometry("Geometry.Status.Check"),
            DisplayState.Failure => FindGeometry("Geometry.Status.Cross"),
            DisplayState.Waiting => FindGeometry("Geometry.Status.ClockHands"),
            DisplayState.Cancelled => FindGeometry("Geometry.Status.Slash"),
            DisplayState.Skipped => FindGeometry("Geometry.Status.Dash"),
            DisplayState.Unknown => FindGeometry("Geometry.Status.Question"),
            _ => null,
        };
        Dot.Data = display switch
        {
            DisplayState.Queued => FindGeometry("Geometry.Status.CenterDot"),
            DisplayState.Unknown => FindGeometry("Geometry.Status.QuestionDot"),
            _ => null,
        };

        // 成功、失敗是實心圓，記號改用深色畫在上面（spec §7.1）。
        if (display is DisplayState.Success or DisplayState.Failure)
        {
            Ring.SetResourceReference(Shape.FillProperty, display == DisplayState.Success ? AccentBrushKey : DangerBrushKey);
            Glyph.SetResourceReference(Shape.StrokeProperty, OnFillBrushKey);
        }

        var label = state?.Label ?? string.Empty;
        AutomationProperties.SetName(this, label);
        ToolTip = string.IsNullOrEmpty(label) ? null : label;

        UpdateSpin();
    }

    /// <summary>
    /// 顏色（spec §16.2）：成功 Accent、失敗 Danger；排隊、進行中、等待核准與還沒完成的未知狀態 Warning；
    /// 取消、略過與完成後的未知 Text Muted（Tag 為 null）。
    /// </summary>
    private static string? ToneOf(RunState? state) => state switch
    {
        null => null,
        { Display: DisplayState.Success } => SuccessTone,
        { Display: DisplayState.Failure } => FailureTone,
        { Category: RunCategory.Pending or RunCategory.Waiting } => WarningTone,
        _ => null,
    };

    private Geometry? FindGeometry(string key) => TryFindResource(key) as Geometry;

    /// <summary>只有畫面上的「進行中」圖示旋轉；關閉動畫效果時停在靜態圓弧。</summary>
    private void UpdateSpin()
    {
        var spin = IsLoaded && IsVisible && State?.Display == DisplayState.InProgress && SystemParameters.ClientAreaAnimation;
        if (!spin)
        {
            Spin.BeginAnimation(RotateTransform.AngleProperty, null);
            Spin.Angle = 0;
            return;
        }

        if (Spin.HasAnimatedProperties)
        {
            return;
        }

        var animation = new DoubleAnimation(0, FullTurnDegrees, SpinPeriod) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, SpinFramesPerSecond);
        Spin.BeginAnimation(RotateTransform.AngleProperty, animation);
    }
}
