using System.Diagnostics;
using System.Linq.Expressions;
using Nop.Core;
using Nop.Core.Caching;

namespace Nop.Data;

/// <summary>
/// A generic decorator that wraps EntityRepository&lt;TEntity&gt; with OpenTelemetry tracing spans.
/// Each CRUD operation creates an Activity span recording entity type, operation, and duration.
/// Since Linq2DB does not emit diagnostic events, this is the primary data-layer instrumentation.
/// </summary>
public partial class InstrumentedRepository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private static readonly ActivitySource _activitySource = new("NopCommerce.Data", "1.0.0");
    private readonly EntityRepository<TEntity> _inner;
    private static readonly string _entityName = typeof(TEntity).Name;

    public InstrumentedRepository(EntityRepository<TEntity> inner)
    {
        _inner = inner;
    }

    private Activity StartSpan(string operation)
    {
        var activity = _activitySource.StartActivity($"Repository.{operation}");
        activity?.SetTag("db.system", "linq2db");
        activity?.SetTag("db.operation", operation);
        activity?.SetTag("db.entity", _entityName);
        return activity;
    }

    public async Task<TEntity> GetByIdAsync(int? id, Func<ICacheKeyService, CacheKey> getCacheKey = null, bool includeDeleted = true, bool useShortTermCache = false)
    {
        using var activity = StartSpan("GetById");
        activity?.SetTag("db.entity_id", id);
        return await _inner.GetByIdAsync(id, getCacheKey, includeDeleted, useShortTermCache);
    }

    public async Task<IList<TEntity>> GetByIdsAsync(IList<int> ids, Func<ICacheKeyService, CacheKey> getCacheKey = null, bool includeDeleted = true)
    {
        using var activity = StartSpan("GetByIds");
        activity?.SetTag("db.ids_count", ids?.Count ?? 0);
        return await _inner.GetByIdsAsync(ids, getCacheKey, includeDeleted);
    }

    public async Task<IList<TEntity>> GetAllAsync(
        Func<IQueryable<TEntity>, IQueryable<TEntity>> func = null,
        Func<ICacheKeyService, CacheKey> getCacheKey = null,
        bool includeDeleted = true)
    {
        using var activity = StartSpan("GetAll");
        var result = await _inner.GetAllAsync(func, getCacheKey, includeDeleted);
        activity?.SetTag("db.result_count", result?.Count ?? 0);
        return result;
    }

    public async Task<IList<TEntity>> GetAllAsync(
        Func<IQueryable<TEntity>, Task<IQueryable<TEntity>>> func = null,
        Func<ICacheKeyService, CacheKey> getCacheKey = null,
        bool includeDeleted = true)
    {
        using var activity = StartSpan("GetAll");
        var result = await _inner.GetAllAsync(func, getCacheKey, includeDeleted);
        activity?.SetTag("db.result_count", result?.Count ?? 0);
        return result;
    }

    public async Task<IList<TEntity>> GetAllAsync(
        Func<IQueryable<TEntity>, Task<IQueryable<TEntity>>> func,
        Func<ICacheKeyService, Task<CacheKey>> getCacheKey,
        bool includeDeleted = true)
    {
        using var activity = StartSpan("GetAll");
        var result = await _inner.GetAllAsync(func, getCacheKey, includeDeleted);
        activity?.SetTag("db.result_count", result?.Count ?? 0);
        return result;
    }

    public async Task<IPagedList<TEntity>> GetAllPagedAsync(
        Func<IQueryable<TEntity>, IQueryable<TEntity>> func = null,
        int pageIndex = 0, int pageSize = int.MaxValue,
        bool getOnlyTotalCount = false, bool includeDeleted = true)
    {
        using var activity = StartSpan("GetAllPaged");
        activity?.SetTag("db.page_index", pageIndex);
        activity?.SetTag("db.page_size", pageSize);
        var result = await _inner.GetAllPagedAsync(func, pageIndex, pageSize, getOnlyTotalCount, includeDeleted);
        activity?.SetTag("db.total_count", result?.TotalCount ?? 0);
        return result;
    }

    public async Task<IPagedList<TEntity>> GetAllPagedAsync(
        Func<IQueryable<TEntity>, Task<IQueryable<TEntity>>> func = null,
        int pageIndex = 0, int pageSize = int.MaxValue,
        bool getOnlyTotalCount = false, bool includeDeleted = true)
    {
        using var activity = StartSpan("GetAllPaged");
        activity?.SetTag("db.page_index", pageIndex);
        activity?.SetTag("db.page_size", pageSize);
        var result = await _inner.GetAllPagedAsync(func, pageIndex, pageSize, getOnlyTotalCount, includeDeleted);
        activity?.SetTag("db.total_count", result?.TotalCount ?? 0);
        return result;
    }

    public async Task InsertAsync(TEntity entity, bool publishEvent = true)
    {
        using var activity = StartSpan("Insert");
        await _inner.InsertAsync(entity, publishEvent);
    }

    public async Task InsertAsync(IList<TEntity> entities, bool publishEvent = true)
    {
        using var activity = StartSpan("InsertBulk");
        activity?.SetTag("db.count", entities?.Count ?? 0);
        await _inner.InsertAsync(entities, publishEvent);
    }

    public async Task<TEntity> LoadOriginalCopyAsync(TEntity entity)
    {
        using var activity = StartSpan("LoadOriginalCopy");
        return await _inner.LoadOriginalCopyAsync(entity);
    }

    public async Task UpdateAsync(TEntity entity, bool publishEvent = true)
    {
        using var activity = StartSpan("Update");
        await _inner.UpdateAsync(entity, publishEvent);
    }

    public async Task UpdateAsync(IList<TEntity> entities, bool publishEvent = true)
    {
        using var activity = StartSpan("UpdateBulk");
        activity?.SetTag("db.count", entities?.Count ?? 0);
        await _inner.UpdateAsync(entities, publishEvent);
    }

    public async Task DeleteAsync(TEntity entity, bool publishEvent = true)
    {
        using var activity = StartSpan("Delete");
        await _inner.DeleteAsync(entity, publishEvent);
    }

    public async Task DeleteAsync(IList<TEntity> entities, bool publishEvent = true)
    {
        using var activity = StartSpan("DeleteBulk");
        activity?.SetTag("db.count", entities?.Count ?? 0);
        await _inner.DeleteAsync(entities, publishEvent);
    }

    public async Task<int> DeleteAsync(Expression<Func<TEntity, bool>> predicate)
    {
        using var activity = StartSpan("DeleteByPredicate");
        return await _inner.DeleteAsync(predicate);
    }

    public async Task TruncateAsync(bool resetIdentity = false)
    {
        using var activity = StartSpan("Truncate");
        await _inner.TruncateAsync(resetIdentity);
    }

    #region Properties

    public IQueryable<TEntity> Table => _inner.Table;

    #endregion
}
