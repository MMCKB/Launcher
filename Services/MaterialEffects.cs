using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;

namespace WinUI3Desktop;

/// <summary>
/// 四种真实材质（Composition 实时效果，直接采样窗口内的壁纸绘制）：
/// - GaussianBlur : 中等模糊 + 原饱和
/// - Acrylic      : XAML AcrylicBrush（实时模糊 + 着色 + 回退色）
/// - Mica         : 大半径重度模糊 + 重降饱和 + 微压暗（云母观感）
/// - LiquidGlass  : Apple 式液态玻璃——低模糊提亮底 + 中心放大层（内容居中放大、
///                  边缘相对压缩 = 折射感）+ 斜向高光
/// 效果链使用开源库 Win2D（github.com/microsoft/Win2D）的效果定义，BackdropBrush 采样窗口内壁纸。
/// 着色层刻意保持半透明（alpha 上限约 130），否则会盖住效果导致四种材质观感趋同。
/// 任何效果链异常都会退回 AcrylicBrush——材质问题绝不能拖垮桌面外壳。
/// </summary>
public static class MaterialEffects
{
    /// <summary>大面板（启动台）应用所选材质的效果链。</summary>
    public static void Apply(FrameworkElement host, SurfaceMaterialKind kind, LauncherOptions options, bool isLight)
    {
        try
        {
            ApplyCore(host, kind, options, isLight);
        }
        catch (Exception ex)
        {
            LogMaterialFallback(ex);
            try
            {
                ElementCompositionPreview.SetElementChildVisual(host, null);
                SetBackground(host, CreateAcrylicBrush(options, isLight));
            }
            catch
            {
                // 兜底失败时保留现状，避免二次异常。
            }
        }
    }

    /// <summary>小圆角控件（芯片）：始终使用 AcrylicBrush——效果刷无法参与圆角裁剪
    /// （19041 无 RoundedRectangleClip、Shape 填充不支持效果刷），Acrylic 的实时模糊
    /// 配合控件自身的圆角在任何半径下都正确。</summary>
    public static void ApplyChip(Control host, LauncherOptions options, bool isLight)
    {
        try
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            host.Background = CreateAcrylicBrush(options, isLight);
        }
        catch
        {
            // 芯片材质失败保持现状。
        }
    }

    /// <summary>浮层面板（圆角 Border）：亚克力实时模糊背景，主题感知。</summary>
    public static void ApplyPanel(FrameworkElement host, LauncherOptions options, bool isLight)
    {
        try
        {
            SetBackground(host, CreateAcrylicBrush(options, isLight));
        }
        catch
        {
            // 面板背景失败保持 XAML 默认。
        }
    }

    private static void ApplyCore(FrameworkElement host, SurfaceMaterialKind kind, LauncherOptions options, bool isLight)
    {
        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        var compositor = hostVisual.Compositor;
        SetBackground(host, null);

        // 各材质档位：模糊半径 / 饱和度 / 曝光——刻意拉开差距，切换一眼可辨
        var (blur, saturation, exposure) = kind switch
        {
            SurfaceMaterialKind.Mica => (90f, 0.20f, -0.03f),
            SurfaceMaterialKind.LiquidGlass => (12f, 1.60f, 0.05f),
            _ => (30f, 1.00f, 0f)
        };
        var isLiquidGlass = kind == SurfaceMaterialKind.LiquidGlass;

        var backdrop = compositor.CreateBackdropBrush();
        var container = compositor.CreateContainerVisual();

        // 底层：全幅磨砂（液态玻璃时它只露出来边缘环）
        container.Children.InsertAtTop(CreateEffectLayer(compositor, backdrop, blur, saturation, exposure, hostVisual));

        // 液态玻璃：中心放大层——内容居中放大 6%，边缘相对压缩，形成折射环
        if (isLiquidGlass)
        {
            const float band = 44f;
            var zoomSprite = compositor.CreateSpriteVisual();
            zoomSprite.Brush = CreateFrostBrush(compositor, blur + 10, 1.80f, 0.12f);
            zoomSprite.Offset = new Vector3(band, band, 0);

            var sizeExpression = compositor.CreateExpressionAnimation("host.Size - Vector2(88.0, 88.0)");
            sizeExpression.SetReferenceParameter("host", hostVisual);
            zoomSprite.StartAnimation("Size", sizeExpression);

            var scaleExpression = compositor.CreateExpressionAnimation(
                "Vector3(host.Size.X / (host.Size.X - 88.0), host.Size.Y / (host.Size.Y - 88.0), 1.0)");
            scaleExpression.SetReferenceParameter("host", hostVisual);
            zoomSprite.StartAnimation("Scale", scaleExpression);

            var centerExpression = compositor.CreateExpressionAnimation(
                "Vector3((host.Size.X - 88.0) * 0.5, (host.Size.Y - 88.0) * 0.5, 0.0)");
            centerExpression.SetReferenceParameter("host", hostVisual);
            zoomSprite.StartAnimation("CenterPoint", centerExpression);

            container.Children.InsertAtTop(zoomSprite);
        }

        // 着色层：半透明度控制整体通透度（上限刻意压低，保证效果透出来）
        var tint = isLight ? Color.FromArgb(255, 242, 246, 253) : Color.FromArgb(255, 23, 35, 60);
        var tintAlpha = (byte)Math.Clamp(options.MaterialOpacity * 130, 20, 160);
        var scrimAlpha = (byte)Math.Clamp(options.TintStrength * 80, 0, 100);
        var scrim = isLight
            ? Color.FromArgb(scrimAlpha, 255, 255, 255)
            : Color.FromArgb(scrimAlpha, 0, 0, 10);

        container.Children.InsertAtTop(CreateColorLayer(compositor, compositor.CreateColorBrush(Color.FromArgb(tintAlpha, tint.R, tint.G, tint.B)), hostVisual));
        container.Children.InsertAtTop(CreateColorLayer(compositor, compositor.CreateColorBrush(scrim), hostVisual));

        if (isLiquidGlass)
        {
            container.Children.InsertAtTop(CreateColorLayer(compositor, CreateSheenBrush(compositor), hostVisual));
        }

        ElementCompositionPreview.SetElementChildVisual(host, container);
    }

    /// <summary>一层"磨砂玻璃"：高斯模糊 + 饱和度 +（可选）曝光，采样窗口内壁纸。</summary>
    private static Visual CreateEffectLayer(Compositor compositor, CompositionBackdropBrush backdrop, float blur, float saturation, float exposure, Visual hostVisual)
    {
        Windows.Graphics.Effects.IGraphicsEffect chain = new SaturationEffect
        {
            Saturation = saturation,
            Source = new ExposureEffect
            {
                Exposure = exposure,
                Source = new GaussianBlurEffect
                {
                    BlurAmount = blur,
                    BorderMode = EffectBorderMode.Hard,
                    Source = new CompositionEffectSourceParameter("backdrop")
                }
            }
        };

        var factory = compositor.CreateEffectFactory(chain);
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", backdrop);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        BindSize(sprite, hostVisual);
        return sprite;
    }

    private static CompositionBrush CreateFrostBrush(Compositor compositor, float blur, float saturation, float exposure)
    {
        Windows.Graphics.Effects.IGraphicsEffect chain = new SaturationEffect
        {
            Saturation = saturation,
            Source = new ExposureEffect
            {
                Exposure = exposure,
                Source = new GaussianBlurEffect
                {
                    BlurAmount = blur,
                    BorderMode = EffectBorderMode.Hard,
                    Source = new CompositionEffectSourceParameter("backdrop")
                }
            }
        };

        var factory = compositor.CreateEffectFactory(chain);
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
        return brush;
    }

    /// <summary>纯色/渐变层（CompositionBrush 直接可用），尺寸跟随宿主。</summary>
    private static Visual CreateColorLayer(Compositor compositor, CompositionBrush brush, Visual hostVisual)
    {
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        BindSize(sprite, hostVisual);
        return sprite;
    }

    private static void BindSize(Visual target, Visual hostVisual)
    {
        var expression = target.Compositor.CreateExpressionAnimation("host.Size");
        expression.SetReferenceParameter("host", hostVisual);
        target.StartAnimation("Size", expression);
    }

    private static void SetBackground(FrameworkElement host, Brush? brush)
    {
        switch (host)
        {
            case Border border:
                border.Background = brush;
                break;
            case Control control:
                control.Background = brush;
                break;
        }
    }

    /// <summary>液态玻璃的斜向高光层。</summary>
    private static CompositionBrush CreateSheenBrush(Compositor compositor)
    {
        var brush = compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0, 0);
        brush.EndPoint = new Vector2(1, 1);
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(70, 255, 255, 255)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0.35f, Color.FromArgb(0, 255, 255, 255)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0.65f, Color.FromArgb(0, 255, 255, 255)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(50, 255, 255, 255)));
        return brush;
    }

    /// <summary>亚克力：透明度直接驱动着色 alpha（可见变化），着色强度驱动色彩浓度。</summary>
    private static Brush CreateAcrylicBrush(LauncherOptions options, bool isLight)
    {
        var opacity = (byte)Math.Clamp((int)Math.Round(options.MaterialOpacity * 255), 80, 250);
        var tintStrength = Math.Clamp(options.MaterialOpacity * (0.35 + 0.55 * options.TintStrength), 0.15, 0.90);

        return isLight
            ? new AcrylicBrush
            {
                TintColor = Color.FromArgb(opacity, 242, 246, 253),
                TintOpacity = tintStrength,
                FallbackColor = Color.FromArgb(opacity, 242, 246, 253)
            }
            : new AcrylicBrush
            {
                TintColor = Color.FromArgb(opacity, 34, 52, 90),
                TintOpacity = tintStrength,
                FallbackColor = Color.FromArgb(opacity, 34, 52, 90)
            };
    }

    /// <summary>材质兜底诊断日志：%LOCALAPPDATA%\MyDock\material-fallback.txt</summary>
    private static void LogMaterialFallback(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyDock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "material-fallback.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响兜底。
        }
    }
}
