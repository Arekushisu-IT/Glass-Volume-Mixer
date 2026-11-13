using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace GlassMixer.App;

/// <summary>
/// The adjustable blur behind the mixer, drawn by Windows itself.
///
/// Windows.UI.Composition is the system's own drawing layer (Start menu, taskbar and
/// acrylic all use it). We add one rectangle ("visual") to our window and paint it with:
///   host backdrop brush   a live view of whatever is behind the window
///   -> Gaussian blur      a Direct2D effect; its strength is the slider's value
///   -> rounded clip       the panel's 18px corners, anti-aliased
///
/// Everything happens on the graphics card inside the Desktop Window Manager, so the
/// mixer shows up normally in screenshots and screen recordings, and nothing is copied
/// from the screen. (The first version copied the screen 30 times a second and had to
/// hide the window from capture to do it.)
/// </summary>
sealed class GlassBackdrop
{
    Compositor? _compositor;
    DesktopWindowTarget? _target;   // must stay alive: it ties the visuals to the window
    SpriteVisual? _visual;
    CompositionEffectBrush? _brush;
    CompositionRoundedRectangleGeometry? _shape;
    static IntPtr s_dispatcherQueue;

    public bool IsAttached => _visual is not null;

    /// <summary>Adds the blur visual to the window. False if this Windows can't do it.</summary>
    public bool Attach(IntPtr hwnd)
    {
        try
        {
            EnsureDispatcherQueue();

            // Lets a brush in this window see what is behind the window (Windows 11).
            int enable = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, ref enable, sizeof(int));

            _compositor = new Compositor();
            // "false" = draw beneath child windows: the blur sits under the web page.
            _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, 0, out IntPtr rawTarget);
            _target = DesktopWindowTarget.FromAbi(rawTarget);
            Marshal.Release(rawTarget);

            var factory = _compositor.CreateEffectFactory(new GaussianBlurEffect(), new[] { BlurProperty });
            _brush = factory.CreateBrush();
            _brush.SetSourceParameter(GaussianBlurEffect.SourceName, _compositor.CreateHostBackdropBrush());

            _shape = _compositor.CreateRoundedRectangleGeometry();
            _visual = _compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            _visual.Clip = _compositor.CreateGeometricClip(_shape);
            _visual.IsVisible = false;

            var root = _compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One; // fill the window
            root.Children.InsertAtTop(_visual);
            _target.Root = root;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Composition blur is not available: " + ex);
            _visual = null;
            return false;
        }
    }

    /// <summary>Where the panel is inside the window, in physical pixels.</summary>
    public void SetBounds(float x, float y, float width, float height, float cornerRadius)
    {
        if (_visual is null || _shape is null) return;
        _visual.Offset = new Vector3(x, y, 0);
        _visual.Size = new Vector2(width, height);
        _shape.Size = new Vector2(width, height);
        _shape.CornerRadius = new Vector2(cornerRadius);
    }

    /// <summary>Blur strength in physical pixels. 0 hides the blur (fully clear glass).</summary>
    public void SetBlur(float amount)
    {
        if (_visual is null || _brush is null) return;
        _visual.IsVisible = amount > 0;
        if (amount > 0) _brush.Properties.InsertScalar(BlurProperty, amount);
    }

    const string BlurProperty = GaussianBlurEffect.EffectName + ".BlurAmount";

    // ----- plumbing -----

    // A Compositor needs a DispatcherQueue on its thread (the Windows Runtime's message loop hook).
    static void EnsureDispatcherQueue()
    {
        if (s_dispatcherQueue != IntPtr.Zero) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 2,  // DQTAT_COM_STA: WPF's UI thread
        };
        CreateDispatcherQueueController(options, out s_dispatcherQueue);
    }

    const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // The classic COM door from a Compositor to a Win32 window.
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, int isTopmost, out IntPtr target);
    }
}

/// <summary>
/// Describes a Direct2D Gaussian blur to the Compositor. Normally the Win2D library
/// provides this class; Win2D doesn't fit a plain WPF app, so this is the small part of
/// it we need. The Compositor asks two things of an effect:
///   - IGraphicsEffect: its name (the Windows Runtime part, handled by the projection),
///   - IGraphicsEffectD2D1Interop: which Direct2D effect it is and its property values
///     (a classic COM interface, built by hand at the bottom of this file).
/// </summary>
sealed unsafe class GaussianBlurEffect : IGraphicsEffect, IGraphicsEffectSource, ICustomQueryInterface
{
    public const string EffectName = "Blur";
    public const string SourceName = "backdrop";

    public string Name { get => EffectName; set { } }

    // Asked for an interface the projection doesn't know: hand out our hand-built one.
    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == IID_IGraphicsEffectD2D1Interop)
        {
            ppv = s_interop;
            return CustomQueryInterfaceResult.Handled;
        }
        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_IGraphicsEffectD2D1Interop = new("2FC57384-A068-44D7-A331-30982FCF7177");
    static readonly Guid IID_IPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    static readonly Guid CLSID_D2D1GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    static readonly CompositionEffectSourceParameter s_source = new(SourceName);
    static readonly IntPtr s_interop = BuildInterop();

    const int S_OK = 0, E_NOINTERFACE = unchecked((int)0x80004002), E_INVALIDARG = unchecked((int)0x80070057), E_FAIL = unchecked((int)0x80004005);

    // A COM object is a pointer to a table of function pointers ("vtable"), in a fixed order.
    // This one lives for the whole run of the app, so AddRef and Release don't count.
    static IntPtr BuildInterop()
    {
        var vtable = (IntPtr*)NativeMemory.Alloc(9, (nuint)sizeof(IntPtr));
        vtable[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRef;
        vtable[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Release;
        vtable[3] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, int>)&GetEffectId;
        vtable[4] = (IntPtr)(delegate* unmanaged<IntPtr, char*, uint*, int*, int>)&GetNamedPropertyMapping;
        vtable[5] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int>)&GetPropertyCount;
        vtable[6] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetProperty;
        vtable[7] = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetSource;
        vtable[8] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int>)&GetSourceCount;

        var instance = (IntPtr*)NativeMemory.Alloc(1, (nuint)sizeof(IntPtr));
        instance[0] = (IntPtr)vtable;
        return (IntPtr)instance;
    }

    [UnmanagedCallersOnly]
    static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IID_IUnknown || *iid == IID_IGraphicsEffectD2D1Interop) { *result = self; return S_OK; }
        *result = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly] static uint AddRef(IntPtr self) => 1;
    [UnmanagedCallersOnly] static uint Release(IntPtr self) => 1;

    [UnmanagedCallersOnly]
    static int GetEffectId(IntPtr self, Guid* id) { *id = CLSID_D2D1GaussianBlur; return S_OK; }

    // "Blur.BlurAmount" in code -> Direct2D property 0 (standard deviation), passed straight through.
    [UnmanagedCallersOnly]
    static int GetNamedPropertyMapping(IntPtr self, char* name, uint* index, int* mapping)
    {
        if (!new string(name).Equals("BlurAmount", StringComparison.OrdinalIgnoreCase)) return E_INVALIDARG;
        *index = 0;
        *mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
        return S_OK;
    }

    [UnmanagedCallersOnly]
    static int GetPropertyCount(IntPtr self, uint* count) { *count = 3; return S_OK; }

    // Starting values of the three Gaussian blur properties.
    [UnmanagedCallersOnly]
    static int GetProperty(IntPtr self, uint index, IntPtr* value)
    {
        try
        {
            object boxed = index switch
            {
                0 => PropertyValue.CreateSingle(20f),  // StandardDeviation: the blur strength
                1 => PropertyValue.CreateUInt32(1),    // Optimization: balanced
                2 => PropertyValue.CreateUInt32(1),    // BorderMode: hard (don't fade out at the edges)
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            IntPtr inspectable = MarshalInspectable<object>.FromManaged(boxed);
            Guid iid = IID_IPropertyValue;
            int hr = Marshal.QueryInterface(inspectable, in iid, out *value);
            Marshal.Release(inspectable);
            return hr;
        }
        catch { *value = IntPtr.Zero; return E_FAIL; }
    }

    // The blur's input: a named placeholder, filled with the host backdrop brush later.
    [UnmanagedCallersOnly]
    static int GetSource(IntPtr self, uint index, IntPtr* source)
    {
        try { *source = MarshalInterface<IGraphicsEffectSource>.FromManaged(s_source); return S_OK; }
        catch { *source = IntPtr.Zero; return E_FAIL; }
    }

    [UnmanagedCallersOnly]
    static int GetSourceCount(IntPtr self, uint* count) { *count = 1; return S_OK; }
}
