using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Overlay OPCIONES: capa dentro de la ventana (modal, Esc = Cancelar).</summary>
public partial class OptionsOverlay : UserControl
{
    private OptionsViewModel? _viewModel;

    public OptionsOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.FocusUsernameRequested -= OnFocusUsernameRequested;
            _viewModel = DataContext as OptionsViewModel;
            if (_viewModel is not null)
                _viewModel.FocusUsernameRequested += OnFocusUsernameRequested;
        };
    }

    /// <summary>GUARDAR con nombre inválido: pone el foco en el campo para corregirlo.</summary>
    private void OnFocusUsernameRequested(object? sender, EventArgs e)
    {
        UsernameBox.Focus();
        UsernameBox.CaretIndex = UsernameBox.Text.Length;
    }

    /// <summary>Al abrir, el foco entra en el panel para que Tab no salga a la ventana de debajo.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }
}
