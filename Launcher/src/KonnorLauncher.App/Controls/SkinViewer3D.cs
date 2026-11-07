using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KonnorLauncher.Core.Players;

namespace KonnorLauncher.App.Controls;

/// <summary>
/// Visor 3D de una skin de Minecraft: el jugador con sus capas base y exteriores, girando despacio y
/// respirando. Con el botón pulsado se para el giro, se orienta libremente (aunque el ratón salga del
/// recuadro) y la rueda acerca o aleja; al soltar, la inclinación vuelve a la inicial, el zoom al elegido
/// (<see cref="Zoom"/>) y sigue girando. La textura debe venir ya normalizada a 64×64
/// (<see cref="SkinTexture.Normalize"/>). Las coordenadas de cada caja siguen el diseño de las skins
/// del juego: arriba, abajo, lado derecho, frente, lado izquierdo y espalda.
/// </summary>
public sealed class SkinViewer3D : Grid
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(SkinComposer.Pixels), typeof(SkinViewer3D), new PropertyMetadata(null, (d, _) => ((SkinViewer3D)d).Rebuild()));

    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(
        nameof(Slim), typeof(bool), typeof(SkinViewer3D), new PropertyMetadata(false, (d, _) => ((SkinViewer3D)d).Rebuild()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(SkinViewer3D), new PropertyMetadata(1.0, (d, _) => ((SkinViewer3D)d).OnZoomChanged()));

    /// <summary>
    /// Zoom elegido (1 = distancia inicial; 2 = el doble de cerca). Es el zoom al que se vuelve al soltar;
    /// mientras se arrastra manda la rueda.
    /// </summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Textura de 64×64 (BGRA) ya normalizada.</summary>
    public SkinComposer.Pixels? Skin
    {
        get => (SkinComposer.Pixels?)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    /// <summary>Modelo delgado: brazos de 3 píxeles.</summary>
    public bool Slim
    {
        get => (bool)GetValue(SlimProperty);
        set => SetValue(SlimProperty, value);
    }

    // Posición inicial (a la que se vuelve al soltar) y límites del giro y del zoom
    private const double RestYaw = -28;
    private const double RestPitch = 0;
    private const double RestDistance = 76;
    private const double IdleDegreesPerSecond = 24;
    private const double MinDistance = 36;
    private const double MaxDistance = 120;
    private const double DegreesPerPixel = 0.8;

    /// <summary>Rapidez de la vuelta a la posición inicial (mayor = más rápida).</summary>
    private const double ReturnSpeed = 7;

    /// <summary>Centro del giro y punto al que mira la cámara: la mitad del jugador.</summary>
    private static readonly Point3D Center = new(0, 16, 0);

    private readonly ModelVisual3D _visual = new();
    private readonly PerspectiveCamera _camera = new() { UpDirection = new Vector3D(0, 1, 0), FieldOfView = 30 };
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), RestYaw);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), RestPitch);

    // Respiración: brazos que se balancean un poco y pecho que sube y baja
    private readonly AxisAngleRotation3D _rightArmSway = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _rightArmSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _leftArmSway = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _leftArmSwing = new(new Vector3D(1, 0, 0), 0);
    private readonly TranslateTransform3D _chest = new();

    private double _distance = RestDistance;
    private Point? _dragStart;
    private double _dragYaw;
    private double _dragPitch;
    private TimeSpan _lastFrame;
    private Window? _window;

    public SkinViewer3D()
    {
        Background = Brushes.Transparent; // para recibir el ratón en toda la superficie
        ClipToBounds = true;

        var viewport = new Viewport3D { IsHitTestVisible = false, Camera = _camera };
        RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0xB8, 0xB8, 0xB8)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x60, 0x60), new Vector3D(-0.35, -0.55, -1)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        var view = new Transform3DGroup();
        view.Children.Add(new RotateTransform3D(_yaw, Center));
        view.Children.Add(new RotateTransform3D(_pitch, Center));
        _visual.Transform = view;
        viewport.Children.Add(_visual);
        Children.Add(viewport);
        UpdateCamera();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) => EndDrag();
        MouseWheel += OnMouseWheel;
        LostMouseCapture += (_, _) => _dragStart = null;
        // Solo se anima mientras se ve (visible y con el launcher sin minimizar): si no, no consume nada.
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
                _window.StateChanged += OnWindowStateChanged;
            UpdateAnimation();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
                _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            CompositionTarget.Rendering -= OnRendering;
        };
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateAnimation();

    private void UpdateAnimation()
    {
        CompositionTarget.Rendering -= OnRendering;
        if (IsVisible && _window is not { WindowState: WindowState.Minimized })
            CompositionTarget.Rendering += OnRendering;
        _lastFrame = TimeSpan.Zero; // al volver, sin saltos
    }

    // ── Animación: respiración y vuelta a la posición inicial ───────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        var elapsed = _lastFrame == TimeSpan.Zero ? 0 : Math.Min((time - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = time;
        var t = time.TotalSeconds;

        // Respiración (como el jugador quieto en el juego): brazos un poco hacia fuera y adelante/atrás,
        // pecho que sube y baja
        var sway = 3 + 3 * Math.Cos(t * 1.8);
        var swing = 3.4 * Math.Sin(t * 1.35);
        _rightArmSway.Angle = -sway;
        _leftArmSway.Angle = sway;
        _rightArmSwing.Angle = swing;
        _leftArmSwing.Angle = -swing;
        _chest.OffsetY = 0.18 * (1 + Math.Sin(t * 1.8));

        // Sin arrastrar: gira despacio y la inclinación y el zoom vuelven suavemente a los iniciales
        if (_dragStart is null && elapsed > 0)
        {
            var k = 1 - Math.Exp(-ReturnSpeed * elapsed);
            _yaw.Angle = (_yaw.Angle + IdleDegreesPerSecond * elapsed) % 360;
            _pitch.Angle += (NearestRestPitch() - _pitch.Angle) * k;
            var distance = _distance + (ZoomDistance - _distance) * k;
            if (Math.Abs(distance - _distance) > 0.001)
            {
                _distance = distance;
                UpdateCamera();
            }
        }
    }

    /// <summary>Distancia de la cámara para <see cref="Zoom"/>, dentro de los límites del zoom.</summary>
    private double ZoomDistance => Math.Clamp(RestDistance / Math.Max(Zoom, 0.01), MinDistance, MaxDistance);

    /// <summary>
    /// Zoom cambiado (con el deslizador): se aplica al momento, sin la transición suave. Mientras se
    /// arrastra no se toca: manda la rueda y al soltar se vuelve a este.
    /// </summary>
    private void OnZoomChanged()
    {
        if (_dragStart is not null)
            return;
        _distance = ZoomDistance;
        UpdateCamera();
    }

    /// <summary>La inclinación inicial más cercana (si se dieron vueltas en vertical, no se deshacen todas).</summary>
    private double NearestRestPitch() => RestPitch + Math.Round((_pitch.Angle - RestPitch) / 360) * 360;

    private void UpdateCamera()
    {
        // La cámara mira al centro desde delante y un poco por encima, a la distancia del zoom
        var direction = new Vector3D(0, -3, -76);
        direction.Normalize();
        _camera.Position = Center - direction * _distance;
        _camera.LookDirection = direction * _distance;
    }

    // ── Ratón ───────────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragYaw = _yaw.Angle;
        _dragPitch = _pitch.Angle;
        CaptureMouse();
        e.Handled = true; // que no arrastre la ventana
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start)
            return;
        // Con el botón pulsado sigue girando aunque el ratón salga del recuadro (está capturado)
        var position = e.GetPosition(this);
        _yaw.Angle = _dragYaw + (position.X - start.X) * DegreesPerPixel;
        _pitch.Angle = _dragPitch + (position.Y - start.Y) * DegreesPerPixel;
    }

    /// <summary>Rueda con el botón pulsado: acerca o aleja.</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_dragStart is null)
            return; // sin pulsar, la rueda desplaza la página como siempre
        _distance = Math.Clamp(_distance * Math.Pow(0.9, e.Delta / 120.0), MinDistance, MaxDistance);
        UpdateCamera();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _dragStart = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    // ── Modelo ──────────────────────────────────────────────────────────────

    /// <summary>Caja: posición mínima, tamaño, origen en la textura (base y exterior), grosor de la capa exterior y su movimiento.</summary>
    private readonly record struct Part(int X, int Y, int Z, int W, int H, int D, int U, int V, int OuterU, int OuterV, double Inflate, Transform3D Move);

    private void Rebuild()
    {
        if (Skin is not { Width: 64, Height: 64 } skin)
        {
            _visual.Content = null;
            return;
        }

        var material = new DiffuseMaterial(CreateBrush(skin));
        material.Freeze();
        var arm = Slim ? 3 : 4;

        // Cada parte con su movimiento; la capa base y la exterior comparten el de su parte.
        var still = Transform3D.Identity;
        var rightArm = ArmTransform(_rightArmSway, _rightArmSwing, new Point3D(-4 - arm / 2.0, 22, 0));
        var leftArm = ArmTransform(_leftArmSway, _leftArmSwing, new Point3D(4 + arm / 2.0, 22, 0));

        // El jugador mira hacia +Z; su lado derecho queda a la izquierda de la pantalla (-X).
        Part[] parts =
        [
            new(-4, 24, -4, 8, 8, 8, 0, 0, 32, 0, 0.5, _chest),                // cabeza y sombrero
            new(-4, 12, -2, 8, 12, 4, 16, 16, 16, 32, 0.25, _chest),           // cuerpo y chaqueta
            new(-4 - arm, 12, -2, arm, 12, 4, 40, 16, 40, 32, 0.25, rightArm), // brazo derecho
            new(4, 12, -2, arm, 12, 4, 32, 48, 48, 48, 0.25, leftArm),         // brazo izquierdo
            new(-4, 0, -2, 4, 12, 4, 0, 16, 0, 32, 0.25, still),               // pierna derecha
            new(0, 0, -2, 4, 12, 4, 16, 48, 0, 48, 0.25, still),               // pierna izquierda
        ];

        var group = new Model3DGroup();
        // Primero todas las capas base y después las exteriores: su transparencia deja ver la base.
        foreach (var outer in new[] { false, true })
        {
            foreach (var part in parts)
            {
                var mesh = new MeshGeometry3D();
                if (outer)
                    AddBox(mesh, part.X, part.Y, part.Z, part.W, part.H, part.D, part.OuterU, part.OuterV, part.Inflate);
                else
                    AddBox(mesh, part.X, part.Y, part.Z, part.W, part.H, part.D, part.U, part.V);
                mesh.Freeze();
                group.Children.Add(new GeometryModel3D(mesh, material) { Transform = part.Move });
            }
        }

        _visual.Content = group;
    }

    /// <summary>Brazo: se balancea desde el hombro y sube y baja con el pecho.</summary>
    private Transform3DGroup ArmTransform(AxisAngleRotation3D sway, AxisAngleRotation3D swing, Point3D shoulder)
    {
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(sway, shoulder));
        transform.Children.Add(new RotateTransform3D(swing, shoulder));
        transform.Children.Add(_chest);
        return transform;
    }

    /// <summary>
    /// La textura se amplía ×8 sin suavizar: así se ve pixelada como en el juego y el filtrado de la
    /// tarjeta gráfica no mezcla los bordes de caras vecinas.
    /// </summary>
    private static ImageBrush CreateBrush(SkinComposer.Pixels skin)
    {
        const int scale = 8;
        var size = 64 * scale;
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                Buffer.BlockCopy(skin.Bgra, ((y / scale) * 64 + x / scale) * 4, data, (y * size + x) * 4, 4);
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, data, size * 4);
        bitmap.Freeze();
        // Coordenadas de textura absolutas (0..1 = la imagen entera), no relativas a cada malla.
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        // Sin suavizar al ampliar: con zoom los píxeles siguen nítidos, como en el juego.
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    private static void AddBox(MeshGeometry3D mesh, double x, double y, double z, int w, int h, int d, int u, int v, double inflate = 0)
    {
        double x0 = x - inflate, y0 = y - inflate, z0 = z - inflate;
        double x1 = x + w + inflate, y1 = y + h + inflate, z1 = z + d + inflate;

        // Cada cara: esquinas vistas desde fuera (arriba-izq., arriba-der., abajo-der., abajo-izq.) y su rectángulo en la textura.
        AddFace(mesh, new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), u + d, v + d, w, h);                 // frente
        AddFace(mesh, new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), u + d + w + d, v + d, w, h);         // espalda
        AddFace(mesh, new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), u, v + d, d, h);                     // lado derecho
        AddFace(mesh, new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), u + d + w, v + d, d, h);             // lado izquierdo
        AddFace(mesh, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), u + d, v, w, d);                     // arriba
        AddFace(mesh, new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), u + d + w, v, w, d);                 // abajo
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D topLeft, Point3D topRight, Point3D bottomRight, Point3D bottomLeft,
        int u, int v, int width, int height)
    {
        // Un poco hacia dentro del rectángulo para no tomar píxeles de la cara de al lado.
        const double inset = 0.02;
        double u0 = (u + inset) / 64, v0 = (v + inset) / 64, u1 = (u + width - inset) / 64, v1 = (v + height - inset) / 64;

        var start = mesh.Positions.Count;
        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(topRight);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(bottomLeft);
        mesh.TextureCoordinates.Add(new Point(u0, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v1));
        mesh.TextureCoordinates.Add(new Point(u0, v1));
        // Sentido antihorario visto desde fuera: solo se dibuja la cara exterior.
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 3);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 1);
    }
}
