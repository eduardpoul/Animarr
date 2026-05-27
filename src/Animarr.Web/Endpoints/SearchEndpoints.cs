using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Free-text search against external metadata sources, projected to the
/// uniform <see cref="IdentificationCandidateDto"/> shape the
/// candidate-picker drawer renders.
/// </summary>
internal static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.SearchTmdb, async (
            SearchRequest request,
            TmdbClient tmdb,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.Ok(new SearchResponse(Array.Empty<IdentificationCandidateDto>()));

            // Pick the most-relevant TMDB endpoint based on the user's type
            // filter — if no preference is given, fall back to /search/multi
            // which returns mixed tv+movie+person.
            var results = request.Type switch
            {
                MediaItemType.Movie  => await tmdb.SearchMovieAsync(request.Query, ct),
                MediaItemType.Series => await tmdb.SearchTvAsync(request.Query, ct),
                _                    => await tmdb.SearchMultiAsync(request.Query, ct),
            };

            var candidates = results.Select(r => new IdentificationCandidateDto(
                Source:        r.MediaType == "movie" ? "tmdb_movie" : "tmdb_tv",
                ExternalId:    r.Id.ToString(),
                Title:         r.DisplayTitle,
                OriginalTitle: r.OriginalTitle ?? r.OriginalName,
                Year:          r.Year,
                PosterUrl:     string.IsNullOrEmpty(r.PosterPath) ? null : $"https://image.tmdb.org/t/p/w342{r.PosterPath}",
                Overview:      r.Overview,
                Confidence:    Math.Clamp(r.VoteAverage / 10.0, 0, 1)))
                .ToArray();

            return Results.Ok(new SearchResponse(candidates));
        });

        app.MapPost(ApiRoutes.SearchMal, async (
            SearchRequest request,
            MalClient mal,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.Ok(new SearchResponse(Array.Empty<IdentificationCandidateDto>()));

            var results = await mal.SearchAsync(request.Query, limit: 10, ct);
            var candidates = results.Select(a => new IdentificationCandidateDto(
                Source:        "mal",
                ExternalId:    a.Id.ToString(),
                Title:         a.EnglishTitle,
                OriginalTitle: a.Title,
                Year:          a.Year,
                PosterUrl:     a.PosterUrl,
                Overview:      a.Synopsis,
                Confidence:    Math.Clamp((a.Mean ?? 0) / 10.0, 0, 1)))
                .ToArray();

            return Results.Ok(new SearchResponse(candidates));
        });

        app.MapPost(ApiRoutes.SearchImdb, async (
            SearchRequest request,
            ImdbSearchClient imdb,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.Ok(new SearchResponse(Array.Empty<IdentificationCandidateDto>()));

            var results = await imdb.SearchTitlesAsync(request.Query, limit: 10, ct);
            var candidates = results.Select(t => new IdentificationCandidateDto(
                Source:        "imdb",
                ExternalId:    t.Id,
                Title:         string.IsNullOrEmpty(t.PrimaryTitle) ? (t.OriginalTitle ?? "") : t.PrimaryTitle,
                OriginalTitle: t.OriginalTitle,
                Year:          t.StartYear,
                PosterUrl:     null,                    // List result doesn't carry poster — fetched on click.
                Overview:      null,                    // Same — plot is on the detail endpoint.
                Confidence:    t.Rating is null ? 0.4 : Math.Clamp(t.Rating.AggregateRating / 10.0, 0, 1)))
                .ToArray();

            return Results.Ok(new SearchResponse(candidates));
        });

        return app;
    }
}
