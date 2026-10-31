using System.Windows;
using System.Windows.Controls;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Overlay para crear o editar una instancia (modal, Esc = Cancelar).</summary>
public partial class InstanceEditorOverlay : UserControl
{
    public InstanceEditorOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is InstanceEditorViewModel old)
                old.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is InstanceEditorViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    /// <summary>Al desplegar la lista de versiones, el foco va al buscador para escribir directamente.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceEditorViewModel.IsVersionListOpen) && sender is InstanceEditorViewModel { IsVersionListOpen: true } vm)
        {
            // Cuando la lista ya está dibujada: foco en el buscador y la versión elegida a la vista.
            Dispatcher.BeginInvoke(() =>
            {
                VersionFilterBox.Focus();
                var selected = vm.VersionOptions.FirstOrDefault(o => o.IsSelected);
                if (selected is not null && VersionList.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement item)
                    item.BringIntoView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Al abrir, el foco va al nombre.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() =>
            {
                NameBox.Focus();
                NameBox.CaretIndex = NameBox.Text.Length;
            });
    }
}
