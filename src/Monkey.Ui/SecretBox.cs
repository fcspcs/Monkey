using System.Windows;
using System.Windows.Controls;

namespace Monkey.Ui;

/// <summary>
/// Ein Geheimnisfeld mit Anzeigen/Verbergen. WPFs PasswordBox kann ihren
/// Inhalt nicht enttarnen, deshalb liegen hier eine PasswordBox und eine
/// TextBox uebereinander und genau eine ist sichtbar. Der Umschalter sitzt
/// als Chip rechts daneben - ein Tippfehler im Masterpasswort sperrt sonst
/// aus, und ein generiertes Passwort muss man ablesen koennen.
/// </summary>
public sealed class SecretBox : Grid
{
    private readonly PasswordBox _hidden = new();
    private readonly TextBox _plain = new() { Visibility = Visibility.Collapsed };
    private readonly Button _toggle = new() { Content = "Show", MinWidth = 56 };

    private bool _revealed;
    private bool _syncing;

    public event RoutedEventHandler? PasswordChanged;

    public SecretBox()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        SetColumn(_toggle, 1);
        _toggle.Margin = new Thickness(8, 0, 0, 0);
        _toggle.VerticalAlignment = VerticalAlignment.Center;

        Children.Add(_hidden);
        Children.Add(_plain);
        Children.Add(_toggle);

        _hidden.PasswordChanged += (_, e) => { if (!_syncing) PasswordChanged?.Invoke(this, e); };
        _plain.TextChanged += (_, e) => { if (!_syncing) PasswordChanged?.Invoke(this, e); };
        _toggle.Click += (_, _) => Reveal(!_revealed);

        // Der Chip-Stil wohnt im Theme des Fensters; beim Einhaengen nachschlagen
        // statt hart verdrahten, damit das Control ohne Theme nicht bricht.
        Loaded += (_, _) =>
        {
            if (_toggle.Style is null && TryFindResource("ChipButton") is Style chip)
                _toggle.Style = chip;
        };
    }

    public string Password
    {
        get => _revealed ? _plain.Text : _hidden.Password;
        set
        {
            if (_revealed) _plain.Text = value;
            else _hidden.Password = value;
        }
    }

    /// <summary>Zeigt das Geheimnis im Klartext - etwa direkt nach dem Erzeugen.</summary>
    public void RevealSecret() => Reveal(true);

    public void Clear()
    {
        // Nur das aktive Feld meldet die Aenderung; das passive wird still geleert.
        _syncing = true;
        if (_revealed) _hidden.Clear(); else _plain.Clear();
        _syncing = false;

        if (_revealed) _plain.Clear(); else _hidden.Clear();
    }

    /// <summary>Der Fokus gehoert ins Eingabefeld, nie auf den Umschalt-Chip.</summary>
    public new void Focus()
    {
        if (_revealed)
        {
            _plain.Focus();
            _plain.CaretIndex = _plain.Text.Length;
        }
        else
        {
            _hidden.Focus();
        }
    }

    private void Reveal(bool reveal)
    {
        if (reveal == _revealed) return;

        _syncing = true;
        if (reveal) _plain.Text = _hidden.Password;
        else _hidden.Password = _plain.Text;
        _syncing = false;

        _revealed = reveal;
        _hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        _plain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        _toggle.Content = reveal ? "Hide" : "Show";

        if (IsKeyboardFocusWithin) Focus();
    }
}
