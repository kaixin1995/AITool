namespace AITool.Application.Common;

/// <summary>
/// 统一的 API 响应包装。新增的 API 控制器统一返回此格式，
/// 便于前端 axios 拦截器统一解包与错误处理。
/// 现有老 API 不强制改造（保持向后兼容，前端适配层做转换）。
/// </summary>
public class ApiResponse
{
    /// <summary>
    /// 业务是否成功（与 HTTP 状态码独立，HTTP 200 但 success=false 表示业务级失败）。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 给用户展示的提示信息（成功时可空，失败时填错误描述）。
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// 错误代码（机器可读，如 "invalid_credentials"、"model_not_found"），便于前端精确分支。
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// 构造成功响应（无数据）。
    /// </summary>
    public static ApiResponse Ok(string? message = null)
        => new() { Success = true, Message = message };

    /// <summary>
    /// 构造成功响应（带数据）。泛型工厂方法，编译器可从实参推断 T，
    /// 支持匿名类型：ApiResponse.Ok(new { foo = 1 })。
    /// </summary>
    public static ApiResponse<T> Ok<T>(T data, string? message = null)
        => new() { Success = true, Data = data, Message = message };

    /// <summary>
    /// 构造失败响应（无数据）。
    /// </summary>
    public static ApiResponse Fail(string message, string? errorCode = null)
        => new() { Success = false, Message = message, ErrorCode = errorCode };

    /// <summary>
    /// 构造失败响应（无数据），返回指定泛型载体类型，便于统一返回类型签名。
    /// </summary>
    public static ApiResponse<T> Fail<T>(string message, string? errorCode = null)
        => new() { Success = false, Message = message, ErrorCode = errorCode };
}

/// <summary>
/// 带 data 的统一响应。所有工厂方法都在基类 <see cref="ApiResponse"/> 上，
/// 此类仅作为携带 Data 的载体类型，不在自身上重复定义工厂（避免重载解析冲突）。
/// </summary>
public sealed class ApiResponse<T> : ApiResponse
{
    /// <summary>
    /// 业务数据载荷。
    /// </summary>
    public T? Data { get; init; }
}
