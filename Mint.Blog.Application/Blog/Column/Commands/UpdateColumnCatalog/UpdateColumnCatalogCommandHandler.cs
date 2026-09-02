using Mint.Blog.Application.Abstractions;
using Mint.Blog.Domain.Blog.Column.Repositories;

namespace Mint.Blog.Application.Blog.Column.Commands.UpdateColumnCatalog;

public sealed class UpdateColumnCatalogCommandHandler(IColumnRepository columnRepository, IUnitOfWork unitOfWork) {
	public async Task HandleAsync(UpdateColumnCatalogCommand command, CancellationToken cancellationToken = default){
		var column = await columnRepository.GetByIdAsync(command.ColumnId, cancellationToken);
		Guard.Against(column is null, ErrorCodes.ColumnNotFound, "Column not found.");

		var normalizedCatalogs = Normalize(command.Catalogs);

		var articleIds = normalizedCatalogs
			.Where(x => x.ArticleId.HasValue && x.ArticleId > 0)
			.Select(x => x.ArticleId!.Value)
			.ToArray();

		var duplicatedArticles = command.Catalogs
			.SelectMany(parent => parent.Children
				.Where(child => child.ArticleId > 0)
				.Select(child => new { child.ArticleId, CatalogTitle = child.Title.Trim() }))
			.GroupBy(x => x.ArticleId)
			.Where(g => g.Count() > 1)
			.ToArray();
		if (duplicatedArticles.Length > 0) {
			var articleTitles = await columnRepository.GetArticleTitlesAsync(duplicatedArticles.Select(x => x.Key).ToArray(), cancellationToken);
			var duplicateMessage = string.Join("；", duplicatedArticles.Select(group => {
				var articleTitle = articleTitles.TryGetValue(group.Key, out var title) ? title : $"ID {group.Key}";
				return $"{string.Join(" 和 ", group.Select(x => $"目录：{x.CatalogTitle}"))} 引用了相同的文章：{articleTitle}";
			}));
			Guard.Against(true, ErrorCodes.ColumnCatalogArticleDuplicate, duplicateMessage);
		}

		if (articleIds.Length > 0) {
			var distinctArticleIds = articleIds.Distinct().ToArray();
			await columnRepository.ValidateArticleIdsAsync(distinctArticleIds, cancellationToken);

			var occupiedArticleIds = await columnRepository.FilterOccupiedArticleIdsAsync(command.ColumnId,
				distinctArticleIds, cancellationToken);
			Guard.Against(occupiedArticleIds.Count > 0, ErrorCodes.ColumnCatalogArticleOccupied,
				$"以下文章 ID 已被其他专栏占用：{string.Join(", ", occupiedArticleIds)}");
		}

		await unitOfWork.BeginTransactionAsync(cancellationToken);
		try {
			await columnRepository.UpdateCatalogAsync(command.ColumnId, normalizedCatalogs, cancellationToken);
			await unitOfWork.CommitAsync(cancellationToken);
		} catch {
			await unitOfWork.RollbackAsync(cancellationToken);
			throw;
		}
	}

	private static IReadOnlyCollection<ColumnCatalogUpsertModel> Normalize(
		IReadOnlyCollection<UpdateColumnCatalogItemCommand> catalogs){
		var result = new List<ColumnCatalogUpsertModel>();

		for (var i = 0; i < catalogs.Count; i++) {
			var parent = catalogs.ElementAt(i);
			result.Add(new ColumnCatalogUpsertModel(parent.Title.Trim(), null, 1, 0, i + 1, parent.IsDeleted));

			for (var j = 0; j < parent.Children.Count; j++) {
				var child = parent.Children.ElementAt(j);
				var articleId = child.ArticleId > 0 ? child.ArticleId : (long?)null;
				result.Add(new ColumnCatalogUpsertModel(child.Title.Trim(), articleId, 2, i + 1, j + 1, child.IsDeleted));
			}
		}

		return result;
	}
}