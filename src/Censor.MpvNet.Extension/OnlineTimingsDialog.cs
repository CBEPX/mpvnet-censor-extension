using System.Diagnostics;
using System.Globalization;
using System.Net;
using Censor.Core;

namespace Censor.MpvNet.Extension;

internal sealed class OnlineTimingsDialog : Form
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<MovieSearchResult>>> _search;
    private readonly Func<string, CancellationToken, Task<TimingPackage>> _load;
    private readonly long _mediaDurationMs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _query = new()
    {
        AccessibleName = "Название фильма",
        Dock = DockStyle.Fill,
    };
    private readonly ListBox _movies = new()
    {
        AccessibleName = "Найденные фильмы",
        Dock = DockStyle.Fill,
    };
    private readonly TextBox _kinopoiskId = new()
    {
        AccessibleName = "ID или ссылка Кинопоиска",
        Width = 220,
    };
    private readonly DataGridView _candidates = CreateCandidatesGrid();
    private readonly Label _status = new()
    {
        AccessibleName = "Состояние онлайн-поиска",
        AutoSize = true,
    };
    private readonly Button _searchButton;
    private readonly Button _loadButton;
    private readonly Button _addButton;
    private readonly Button _markButton;
    private CancellationTokenSource? _requestCancellation;
    private TimingPackage? _package;
    private bool _busy;

    public OnlineTimingsDialog(
        string mediaTitle,
        long mediaDurationMs,
        Func<string, CancellationToken, Task<IReadOnlyList<MovieSearchResult>>> search,
        Func<string, CancellationToken, Task<TimingPackage>> load)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaDurationMs);
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _mediaDurationMs = mediaDurationMs;

        Text = "Онлайн-тайминги";
        AccessibleName = Text;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new(820, 560);
        Size = new(1_060, 700);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _query.Text = mediaTitle.Trim();
        _searchButton = Button("Найти", SearchAsync);
        _loadButton = Button("Показать тайминги", LoadSelectedAsync);
        _addButton = Button("Добавить выбранные интервалы", AddSelected);
        _markButton = Button("Перейти и отметить начало", MarkSelectedPoint);
        _addButton.Enabled = false;
        _markButton.Enabled = false;
        _loadButton.Enabled = false;
        _status.Text = "Проверьте название и нажмите «Найти».";

        var closeButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Закрыть",
        };
        AcceptButton = _searchButton;
        CancelButton = closeButton;

        _movies.SelectedIndexChanged += (_, _) =>
        {
            if (_movies.SelectedItem is MovieChoice choice)
                _kinopoiskId.Text = choice.Movie.Id;
            _loadButton.Enabled = !_busy && _kinopoiskId.TextLength > 0;
        };
        _kinopoiskId.TextChanged += (_, _) =>
        {
            if (_package is not null)
                ClearCandidates();
            _loadButton.Enabled = !_busy && _kinopoiskId.TextLength > 0;
        };
        _kinopoiskId.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter || !_loadButton.Enabled)
                return;
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            LoadSelectedAsync();
        };
        _movies.DoubleClick += (_, _) =>
        {
            if (_loadButton.Enabled)
                LoadSelectedAsync();
        };
        _candidates.SelectionChanged += (_, _) => UpdateCandidateActions();
        _candidates.CellValueChanged += (_, _) => UpdateCandidateActions();
        _candidates.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_candidates.IsCurrentCellDirty)
                _candidates.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var searchRow = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
        };
        searchRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new(SizeType.AutoSize));
        searchRow.Controls.Add(_query, 0, 0);
        searchRow.Controls.Add(_searchButton, 1, 0);

        var idRow = Flow(
            new Label { AutoSize = true, Margin = new(3, 8, 3, 0), Text = "ID или ссылка Кинопоиска:" },
            _kinopoiskId,
            _loadButton);
        var bottom = Flow(
            _addButton,
            _markButton,
            Button("Другие справочники…", ShowReferenceSources),
            closeButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new(10),
            RowCount = 7,
        };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 28));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 72));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Найдите фильм и проверьте тайминги перед добавлением в черновик.",
        }, 0, 0);
        layout.Controls.Add(searchRow, 0, 1);
        layout.Controls.Add(_movies, 0, 2);
        layout.Controls.Add(idRow, 0, 3);
        layout.Controls.Add(_candidates, 0, 4);
        layout.Controls.Add(_status, 0, 5);
        layout.Controls.Add(bottom, 0, 6);
        Controls.Add(layout);
    }

    public IReadOnlyList<TimingCandidate> SelectedIntervals { get; private set; } = [];
    public TimingCandidate? PointToMark { get; private set; }
    public string Source => _package is null
        ? "timings.rte"
        : $"{_package.Source} kp:{_package.SourceMovieId}";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _lifetime.Cancel();
        _requestCancellation?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _requestCancellation?.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private async void SearchAsync()
    {
        var query = _query.Text.Trim();
        if (query.Length == 0)
        {
            _status.Text = "Введите название фильма.";
            return;
        }

        _movies.Items.Clear();
        _kinopoiskId.Clear();
        ClearCandidates();
        await RunRequestAsync("Поиск…", async token =>
        {
            var movies = await _search(query, token);
            if (token.IsCancellationRequested || IsDisposed || Disposing)
                return;
            _movies.BeginUpdate();
            try
            {
                _movies.Items.Clear();
                foreach (var movie in movies)
                    _movies.Items.Add(new MovieChoice(movie, FormatMovie(movie)));
            }
            finally
            {
                _movies.EndUpdate();
            }
            if (_movies.Items.Count > 0)
                _movies.SelectedIndex = 0;
            _status.Text = movies.Count == 0
                ? "Ничего не найдено. Можно вставить ID или ссылку Кинопоиска."
                : $"Найдено: {movies.Count}. Выберите фильм и проверьте длительность.";
        });
    }

    private async void LoadSelectedAsync()
    {
        var id = _kinopoiskId.Text.Trim();
        if (id.Length == 0)
        {
            _status.Text = "Выберите фильм или введите ID Кинопоиска.";
            return;
        }

        ClearCandidates();
        await RunRequestAsync("Загрузка таймингов…", async token =>
        {
            var package = await _load(id, token);
            if (token.IsCancellationRequested || IsDisposed || Disposing)
                return;
            _package = package;
            RenderCandidates(_package.Entries);
        });
    }

    private async Task RunRequestAsync(
        string progress,
        Func<CancellationToken, Task> request)
    {
        _requestCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _requestCancellation = cancellation;
        SetBusy(true, progress);
        try
        {
            await request(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
        }
        catch (MediaSessionChangedException exception)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = exception.Message;
        }
        catch (ArgumentException exception)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = exception.Message;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Слишком много запросов. Повторите через минуту.";
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Сервис онлайн-таймингов занят. Повторите через несколько секунд.";
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Сервис онлайн-таймингов временно недоступен. Повторите позже.";
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == HttpStatusCode.NotFound)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Для этого фильма тайминги не найдены.";
        }
        catch (TimeoutException exception)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = exception.Message;
        }
        catch (Exception)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Не удалось получить данные. Проверьте подключение и выбранный источник.";
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, cancellation))
            {
                _requestCancellation = null;
                if (!IsDisposed && !Disposing)
                    SetBusy(false, _status.Text);
            }
            cancellation.Dispose();
        }
    }

    private void RenderCandidates(IReadOnlyList<TimingCandidate> candidates)
    {
        _candidates.Rows.Clear();
        foreach (var candidate in candidates)
        {
            var importable = IsImportable(candidate);
            var index = _candidates.Rows.Add(
                importable,
                KindText(candidate.Kind),
                FormatTime(candidate.StartMs),
                FormatTime(candidate.EndMs),
                candidate.Status ?? "—",
                candidate.VoteScore?.ToString(CultureInfo.InvariantCulture) ?? "—",
                candidate.Note ?? candidate.RawText);
            var row = _candidates.Rows[index];
            row.Tag = candidate;
            row.Cells["selected"].ReadOnly = !importable;
            if (!importable)
                row.DefaultCellStyle.ForeColor = Color.DimGray;
        }

        var complete = candidates.Count(IsImportable);
        var points = candidates.Count(item => item.Kind == TimingCandidateKind.Point);
        _status.Text = candidates.Count == 0
            ? "Источник не вернул тайминги для этого фильма."
            : $"Интервалов для добавления: {complete}; одиночных отметок: {points}. " +
              "Неполные записи оставлены только для проверки.";
        UpdateCandidateActions();
    }

    private void ClearCandidates()
    {
        _package = null;
        SelectedIntervals = [];
        PointToMark = null;
        _candidates.Rows.Clear();
        UpdateCandidateActions();
    }

    private void AddSelected()
    {
        var selected = _candidates.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Tag is TimingCandidate &&
                Equals(row.Cells["selected"].Value, true))
            .Select(row => (TimingCandidate)row.Tag!)
            .Where(IsImportable)
            .ToArray();
        if (selected.Length == 0)
        {
            _status.Text = "Отметьте хотя бы один законченный интервал.";
            return;
        }
        SelectedIntervals = selected;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void MarkSelectedPoint()
    {
        if (_candidates.CurrentRow?.Tag is not TimingCandidate point ||
            !IsUsablePoint(point))
        {
            _status.Text = "Выберите строку с одиночной отметкой.";
            return;
        }
        PointToMark = point;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateCandidateActions()
    {
        _addButton.Enabled = !_busy && _candidates.Rows.Cast<DataGridViewRow>()
            .Any(row => Equals(row.Cells["selected"].Value, true));
        _markButton.Enabled = !_busy &&
            _candidates.CurrentRow?.Tag is TimingCandidate point &&
            IsUsablePoint(point);
    }

    private bool IsImportable(TimingCandidate candidate) =>
        candidate is
        {
            Kind: TimingCandidateKind.Interval,
            StartMs: >= 0,
            EndMs: not null,
        } &&
        candidate.EndMs > candidate.StartMs &&
        candidate.EndMs <= _mediaDurationMs;

    private bool IsUsablePoint(TimingCandidate candidate) =>
        candidate is
        {
            Kind: TimingCandidateKind.Point,
            StartMs: >= 0,
        } &&
        candidate.StartMs <= _mediaDurationMs;

    private string FormatMovie(MovieSearchResult movie)
    {
        var duration = movie.SourceDurationMs is { } sourceDuration
            ? $" · {FormatDuration(sourceDuration)}"
            : "";
        var mismatch = movie.DurationDiffersFrom(_mediaDurationMs)
                ? " · длительность отличается"
                : "";
        var year = string.IsNullOrWhiteSpace(movie.Year) ||
            movie.Title.Contains($"({movie.Year})", StringComparison.Ordinal)
                ? ""
                : $" ({movie.Year})";
        return $"{movie.Title}{year} · Кинопоиск {movie.Id}{duration}{mismatch}";
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _query.Enabled = !busy;
        _movies.Enabled = !busy;
        _kinopoiskId.Enabled = !busy;
        _candidates.Enabled = !busy;
        _searchButton.Enabled = !busy;
        _loadButton.Enabled = !busy && _kinopoiskId.TextLength > 0;
        _status.Text = status;
        UpdateCandidateActions();
    }

    private void ShowReferenceSources()
    {
        using var dialog = new Form
        {
            Text = "Другие справочники",
            AccessibleName = "Другие справочники",
            StartPosition = FormStartPosition.CenterParent,
            Size = new(620, 420),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var panel = new FlowLayoutPanel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new(12),
            WrapContents = false,
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new(560, 0),
            Text = "Эти сайты открываются только в браузере. CensorPlayer не копирует и не импортирует их данные.",
        });
        foreach (var source in ReferenceSources)
            panel.Controls.Add(Link(source.Label, source.Url));
        var adult = new CheckBox { AutoSize = true, Text = "Показать ссылку 18+" };
        var adultLink = Link(
            "Celebrity Movie Archive (18+)",
            "https://www.celebritymoviearchive.com/");
        adultLink.Visible = false;
        adult.CheckedChanged += (_, _) => adultLink.Visible = adult.Checked;
        panel.Controls.Add(adult);
        panel.Controls.Add(adultLink);
        dialog.Controls.Add(panel);
        dialog.ShowDialog(this);
    }

    private static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { AutoSize = true, Text = text, Tag = url };
        link.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo((string)link.Tag!) { UseShellExecute = true });
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(
                    "Не удалось открыть ссылку в браузере.",
                    "CensorPlayer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
        return link;
    }

    private static DataGridView CreateCandidatesGrid()
    {
        var grid = new DataGridView
        {
            AccessibleName = "Кандидаты таймингов",
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Dock = DockStyle.Fill,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            FillWeight = 35,
            HeaderText = "Добавить",
            Name = "selected",
        });
        grid.Columns.Add("kind", "Тип");
        grid.Columns.Add("start", "Начало");
        grid.Columns.Add("end", "Конец");
        grid.Columns.Add("status", "Статус");
        grid.Columns.Add("votes", "Голоса");
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 220,
            HeaderText = "Описание",
            Name = "note",
        });
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column.Name != "selected")
                column.ReadOnly = true;
        }
        return grid;
    }

    private static string KindText(TimingCandidateKind kind) => kind switch
    {
        TimingCandidateKind.Interval => "Интервал",
        TimingCandidateKind.Point => "Отметка",
        TimingCandidateKind.CleanClaim => "Заявлено: чисто",
        _ => "Текст",
    };

    private static string FormatTime(long? milliseconds)
    {
        if (milliseconds is not { } value)
            return "—";
        var hours = value / 3_600_000;
        var minutes = value / 60_000 % 60;
        var seconds = value / 1_000 % 60;
        var fraction = value % 1_000;
        return FormattableString.Invariant(
            $"{hours:D2}:{minutes:D2}:{seconds:D2}.{fraction:D3}");
    }

    private static string FormatDuration(long milliseconds)
    {
        var hours = milliseconds / 3_600_000;
        var minutes = milliseconds / 60_000 % 60;
        return FormattableString.Invariant($"{hours:D2}:{minutes:D2}");
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { AutoSize = true, Text = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private sealed record MovieChoice(MovieSearchResult Movie, string Display)
    {
        public override string ToString() => Display;
    }

    private static readonly (string Label, string Url)[] ReferenceSources =
    [
        ("Kids-In-Mind", "https://kids-in-mind.com/"),
        ("RunPee", "https://runpee.com/"),
        ("Common Sense Media", "https://www.commonsensemedia.org/"),
        ("DoesTheDogDie", "https://www.doesthedogdie.com/"),
        ("Unconsenting Media", "https://www.unconsentingmedia.org/"),
        ("Подборка таймингов Letterboxd", "https://letterboxd.com/patalahinart/list/ttv-patalahinart-timings-of-ban-moments-for/"),
    ];
}

internal sealed class MediaSessionChangedException : OperationCanceledException
{
    public MediaSessionChangedException(string message)
        : base(message)
    {
    }

    public MediaSessionChangedException(string message, CancellationToken token)
        : base(message, token)
    {
    }
}
