using System.Windows;

namespace KodosadStudio;

public partial class ProjectPickerWindow : Window
{
    public SavedProject? SelectedProject => ProjectsList.SelectedItem as SavedProject;

    public ProjectPickerWindow(IReadOnlyList<SavedProject> projects)
    {
        InitializeComponent();
        ProjectsList.ItemsSource = projects;
        if (projects.Count > 0) ProjectsList.SelectedIndex = 0;
        StatusText.Text = $"{projects.Count} сохранённых проектов";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) { StatusText.Text = "Выберите проект из списка."; return; }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
