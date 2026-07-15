using Microsoft.Maui.Controls.Shapes;

namespace Animarr.App;

/// <summary>
/// Code-built poster card + "see all" tile, shared by the Home library grid and
/// the full-library page. Built in code (not a DataTemplate) so the D-pad OK
/// command is attached programmatically — the most reliable activation path.
/// </summary>
internal static class TvCards
{
    // TV screens are 960dp wide (1080p @ density 2.0), NOT 1920 — sizes here are
    // dp. 120dp posters + 10dp gaps → 7 columns in the 922dp content strip
    // (24dp page margins), so the Home preview is exactly 2 rows: 13 posters +
    // the "see all" tile.
    public const int PosterWidth  = 120;
    public const int PosterHeight = 180;   // 2:3 art — the whole card (web d-poster)
    // Desired visual gap is 10dp, but MAUI's FlexLayout applies child margins
    // TWICE (measured: 16dp margin → 64px gap @density 2.0, 54px @1.7 — a
    // constant ×2, not a density leak), so the margin is set to half.
    public const double CardGap   = 5;

    public static Border BuildPosterCard(CatalogNativePage.PosterItem p)
    {
        var image = new Image { Aspect = Aspect.AspectFill };
        if (!string.IsNullOrEmpty(p.ImageUrl)) image.Source = p.ImageUrl;

        // Web .d-poster__img runs saturate(.85) brightness(.85) — a flat dim is
        // the closest MAUI equivalent, and it keeps the overlay text readable.
        var dim = new BoxView { InputTransparent = true, Color = Color.FromArgb("#21000000") };

        // Web .d-poster__wash: transparent to 40%, then to rgba(10,8,7,.85).
        var shade = new BoxView
        {
            InputTransparent = true,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0.40f),
                    new GradientStop(Color.FromArgb("#D90A0807"), 1.0f),
                },
                new Point(0, 0), new Point(0, 1)),
        };

        var imageGrid = new Grid();
        imageGrid.Add(image);
        imageGrid.Add(dim);
        imageGrid.Add(shade);
        if (p.HasType)
        {
            imageGrid.Add(new Border
            {
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(8),
                Padding = new Thickness(6, 2),
                StrokeThickness = 1,
                Stroke = Color.FromArgb("#1FFFFFFF"),
                StrokeShape = new RoundRectangle { CornerRadius = 3 },
                BackgroundColor = Color.FromArgb("#99000000"),
                Content = new Label
                {
                    Text = p.TypeLabel, FontFamily = "GeistMono", FontSize = 8,
                    TextColor = Colors.White, CharacterSpacing = 1.4,
                },
            });
        }

        // Web .d-poster__overlay: title + meta sit ON the art, bottom-anchored.
        var overlay = new VerticalStackLayout
        {
            InputTransparent = true,
            VerticalOptions = LayoutOptions.End,
            Padding = new Thickness(8, 0, 8, 8),
            Spacing = 3,
            Children =
            {
                new Label
                {
                    Text = p.Title, FontFamily = "ArchivoBlack", FontAttributes = FontAttributes.Bold, FontSize = 10,
                    TextColor = Colors.White, MaxLines = 2, LineHeight = 1.05,
                    LineBreakMode = LineBreakMode.TailTruncation,
                },
            },
        };
        if (p.HasMeta)
        {
            // 7.5 mono fits "2023 · 156 EP · ★ 7.9" in the 104dp text strip —
            // 8.5 truncated the rating tail on most cards.
            overlay.Children.Add(new Label
            {
                Text = p.Meta, FontFamily = "GeistMono", FontSize = 7.5,
                TextColor = Color.FromArgb("#8a91a0"),
                MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation,
            });
        }
        imageGrid.Add(overlay);

        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromArgb("#15171d"),
            WidthRequest = PosterWidth,
            // Fixed height: FlexLayout wrap rows default to Stretch and would
            // otherwise inflate/clip cards to fill the tallest row.
            HeightRequest = PosterHeight,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Content = imageGrid,
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = p.Open });
        return card;
    }

    /// <summary>"Смотреть дальше" resume card, styled after the web NextUpRail:
    /// a TRANSPARENT card — rounded 16:9 thumb with the resume bar + НОВОЕ badge
    /// on the art, then title/meta on the page background below (no dark box).
    /// The Image element is returned via <paramref name="image"/> so the caller
    /// can swap in the episode frame once its authenticated download lands.</summary>
    public static Border BuildContinueCard(CatalogNativePage.PosterItem p, out Image image)
    {
        image = new Image { Aspect = Aspect.AspectFill };

        var imageGrid = new Grid();
        imageGrid.Add(image);
        if (p.IsNew)
        {
            // Web .nur__badge: accent-hi fill with DARK text.
            imageGrid.Add(new Border
            {
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(8),
                Padding = new Thickness(7, 2),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                BackgroundColor = Color.FromArgb("#f5934f"),
                Content = new Label
                {
                    Text = TvL.T("home.new_badge", "Новое", "New").ToUpperInvariant(),
                    FontFamily = "GeistMono", FontSize = 7.5,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#0f1115"), CharacterSpacing = 0.8,
                },
            });
        }
        if (p.HasProgress)
        {
            var bar = new Grid
            {
                HeightRequest = 3.5,
                VerticalOptions = LayoutOptions.End,
                InputTransparent = true,
            };
            bar.Add(new BoxView { Color = Color.FromArgb("#66000000") });
            bar.Add(new BoxView
            {
                Color = Color.FromArgb("#e8772e"),
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = p.ProgressWidth,
            });
            imageGrid.Add(bar);
        }

        // Only the thumb is clipped/rounded — the text sits on the page itself.
        var thumb = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromArgb("#10141a"),
            HeightRequest = 112,
            Content = imageGrid,
        };

        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Colors.Transparent,
            WidthRequest = 200,
            Content = new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    thumb,
                    new Label
                    {
                        Text = p.Title, FontFamily = "Geist", FontSize = 11.5,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White, MaxLines = 1,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        Margin = new Thickness(2, 8, 2, 0),
                    },
                    new Label
                    {
                        Text = p.Meta, FontFamily = "Geist", FontSize = 9.5,
                        TextColor = Color.FromArgb("#8a91a0"),
                        Margin = new Thickness(2, 2, 2, 2),
                    },
                },
            },
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = p.Open });
        return card;
    }

    /// <summary>Section/source filter pill (the web topbar folder chips):
    /// "All | Anime | Dorams | …" above the library grid.</summary>
    public static Border BuildFilterChip(string label, bool active, System.Windows.Input.ICommand command)
    {
        var chip = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = active ? Color.FromArgb("#e8772e") : Color.FromArgb("#151a21"),
            Padding = new Thickness(14, 7),
            StrokeShape = new RoundRectangle { CornerRadius = 15 },
            Content = new Label
            {
                Text = label,
                TextColor = active ? Colors.White : Color.FromArgb("#c7ccd4"),
                FontFamily = "Geist", FontSize = 12.5,
                FontAttributes = active ? FontAttributes.Bold : FontAttributes.None,
                LineBreakMode = LineBreakMode.NoWrap,
                Margin = new Thickness(0, 0, 3, 0),
            },
        };
        chip.Behaviors.Add(new TvFocusBehavior { Radius = 15, Command = command });
        return chip;
    }

    /// <summary>"На этой неделе" airing card, styled after the web ThisWeekRail
    /// (twr__card): surface box with a landscape art zone (air-time top-right,
    /// title bottom-left over a dark gradient) and an EP + status-chip meta row.</summary>
    public static Border BuildWeekCard(
        string title, string when, string episodeLabel, string status,
        string statusLabel, string? imageUrl, System.Windows.Input.ICommand open)
    {
        var art = new Grid { HeightRequest = 98 };
        var image = new Image { Aspect = Aspect.AspectFill };
        if (!string.IsNullOrEmpty(imageUrl)) image.Source = imageUrl;
        art.Add(image);
        art.Add(new BoxView
        {
            InputTransparent = true,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb("#1F000000"), 0f),
                    new GradientStop(Color.FromArgb("#C7000000"), 1f),
                },
                new Point(0, 0), new Point(0, 1)),
        });
        art.Add(new Label
        {
            Text = when, FontFamily = "GeistMono", FontSize = 9,
            FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 6, 8, 0),
        });
        art.Add(new Label
        {
            Text = title, FontFamily = "Geist", FontSize = 10.5,
            FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
            MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(8, 0, 8, 6),
        });

        // Status chip — the web calendar palette (.cal-chip--*).
        var (fg, bg, line) = status switch
        {
            "in-library"    => ("#5fd68a", "#295FD68A", "#5fd68a"),
            "aired-waiting" => ("#e7b04a", "#29E7B04A", "#80E7B04A"),
            _               => ("#8a91a0", "#0DFFFFFF", "#33FFFFFF"),
        };
        var meta = new Grid
        {
            Padding = new Thickness(9, 7, 9, 9),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        meta.Add(new Label
        {
            Text = episodeLabel, FontFamily = "GeistMono", FontSize = 9.5,
            FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#f5934f"),
            VerticalOptions = LayoutOptions.Center,
        }, 0, 0);
        meta.Add(new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb(line),
            BackgroundColor = Color.FromArgb(bg),
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(6, 2),
            VerticalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = statusLabel.ToUpperInvariant(), FontFamily = "GeistMono",
                FontSize = 7, FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb(fg), CharacterSpacing = 0.4,
                LineBreakMode = LineBreakMode.NoWrap,
            },
        }, 1, 0);

        var rows = new Grid
        {
            RowDefinitions = { new RowDefinition(98), new RowDefinition(GridLength.Auto) },
        };
        rows.Add(art, 0, 0);
        rows.Add(meta, 0, 1);

        var card = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#1FFFFFFF"),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromArgb("#15171d"),
            WidthRequest = 190,
            Content = rows,
        };
        card.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = open });
        return card;
    }

    /// <summary>The web library grid's last cell: "Просмотреть все" + title count,
    /// opening the full-library page.</summary>
    public static Border BuildSeeAllTile(int totalCount, System.Windows.Input.ICommand open)
    {
        var tile = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromArgb("#15171d"),
            WidthRequest = PosterWidth,
            HeightRequest = PosterHeight,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "⊞", TextColor = Color.FromArgb("#e8772e"), FontSize = 26,
                        HorizontalOptions = LayoutOptions.Center,
                    },
                    new Label
                    {
                        Text = TvL.T("home.view_all", "Просмотреть все", "View all"),
                        TextColor = Colors.White,
                        FontFamily = "Geist", FontSize = 13,
                        HorizontalOptions = LayoutOptions.Center,
                    },
                    new Label
                    {
                        Text = totalCount.ToString(), TextColor = Color.FromArgb("#8a91a0"),
                        FontFamily = "GeistMono", FontSize = 11,
                        HorizontalOptions = LayoutOptions.Center,
                    },
                },
            },
        };
        tile.Behaviors.Add(new TvFocusBehavior { Radius = 10, Command = open });
        return tile;
    }
}
