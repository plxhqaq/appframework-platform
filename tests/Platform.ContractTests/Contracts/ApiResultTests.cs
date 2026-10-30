using Platform.Contracts;

namespace Platform.ContractTests.Contracts;

/// <summary>统一返回与分页的工厂行为。</summary>
public sealed class ApiResultTests
{
    [Fact]
    public void Ok携带数据且错误码为0()
    {
        var result = ApiResult.Ok(new { value = 42 });

        Assert.True(result.IsSuccess);
        Assert.Equal((int)ApiErrorCode.Ok, result.Code);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void Success没有数据但仍是成功()
    {
        var result = ApiResult.Success();

        Assert.True(result.IsSuccess);
        Assert.Equal((int)ApiErrorCode.Ok, result.Code);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Fail未传消息时用错误码名兜底()
    {
        var result = ApiResult.Fail(ApiErrorCode.IdentityInvalidCredentials);

        Assert.False(result.IsSuccess);
        Assert.Equal((int)ApiErrorCode.IdentityInvalidCredentials, result.Code);
        Assert.Equal(nameof(ApiErrorCode.IdentityInvalidCredentials), result.Message);
    }

    [Fact]
    public void 泛型Fail可以携带字段级详情()
    {
        var details = new Dictionary<string, string> { ["userName"] = "已存在" };
        var result = ApiResult.Fail(ApiErrorCode.ValidationFailed, "校验失败", details);

        Assert.False(result.IsSuccess);
        Assert.Same(details, result.Data);
    }

    [Fact]
    public void 分页默认值被钳制()
    {
        var query = new PageQuery { Page = 0, PageSize = 0 };

        Assert.Equal(1, query.ClampedPage);
        Assert.Equal(PageQuery.DefaultPageSize, query.ClampedPageSize);
        Assert.Equal(0, query.Skip);

        var huge = new PageQuery { Page = 3, PageSize = 10_000 };
        Assert.Equal(PageQuery.MaxPageSize, huge.ClampedPageSize);
        Assert.Equal((3 - 1) * PageQuery.MaxPageSize, huge.Skip);
    }

    [Fact]
    public void 分页结果计算总页数()
    {
        var result = PageResult<int>.Create([1, 2, 3], total: 45, new PageQuery { Page = 2, PageSize = 20 });

        Assert.Equal(3, result.TotalPages);
        Assert.Equal(2, result.Page);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void 空页保留查询参数()
    {
        var result = PageResult<string>.Empty(new PageQuery { Page = 4, PageSize = 10 });

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(4, result.Page);
        Assert.Equal(10, result.PageSize);
    }
}
