using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AITool.Infrastructure.Sites;

/// <summary>
/// 自定义余额提取表达式求值器：在余额接口的 JSON 响应上计算余额数值。
/// <para>
/// 语法（对齐 magpie balanceExpr 的实用子集）：
/// <code>
///   表达式 := 项 (('+' | '-') 项)*
///   项     := 因子 (('*' | '/') 因子)*
///   因子   := 数值 | 路径 | '(' 表达式 ')' | '-' 因子
///   路径   := '$' 字段路径        （$.data.quota、$.balance_infos[0].total_balance）
///           | '$data' 字段路径    （magpie 兼容写法，等价 $.）
/// </code>
/// 路径取值支持数字与数字字符串（"42"）；除以 0、路径缺失、非数值均为错误。
/// </para>
/// </summary>
public static class CustomBalanceExpression
{
    /// <summary>
    /// 在 JSON 响应体上求值表达式，返回余额数值。失败时返回 null 并给出面向用户的原因。
    /// 表达式为空时：根是数字（或数字字符串）直接返回，否则报错。
    /// </summary>
    public static bool TryEvaluate(string? expression, string jsonBody, out double value, out string error)
    {
        value = 0;
        error = string.Empty;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "余额接口响应不是有效 JSON";
            return false;
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            if (TryReadNumber(root, out var bare))
            {
                value = bare;
                return true;
            }

            error = "未配置提取表达式，且响应根不是数值";
            return false;
        }

        try
        {
            var parser = new Parser(expression, root);
            var result = parser.ParseExpression();
            parser.ExpectEnd();
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                error = "表达式计算结果不是有效数值";
                return false;
            }

            value = result;
            return true;
        }
        catch (FormatException ex) when (!string.IsNullOrWhiteSpace(ex.Message))
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读取 JSON 元素的数值：数字、数字字符串（含小数与负数）兼容。</summary>
    private static bool TryReadNumber(JsonElement element, out double value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    private sealed class Parser(string text, JsonElement root)
    {
        private readonly string _text = text;
        private int _pos;

        public double ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '+')
                {
                    _pos++;
                    left += ParseTerm();
                }
                else if (Peek() == '-')
                {
                    _pos++;
                    left -= ParseTerm();
                }
                else
                {
                    return left;
                }
            }
        }

        private double ParseTerm()
        {
            var left = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '*')
                {
                    _pos++;
                    left *= ParseFactor();
                }
                else if (Peek() == '/')
                {
                    _pos++;
                    var right = ParseFactor();
                    if (Math.Abs(right) < double.Epsilon)
                    {
                        throw new FormatException("表达式除以 0");
                    }

                    left /= right;
                }
                else
                {
                    return left;
                }
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            var current = Peek();
            if (current == '-')
            {
                _pos++;
                return -ParseFactor();
            }

            if (current == '(')
            {
                _pos++;
                var value = ParseExpression();
                if (Peek() != ')')
                {
                    throw new FormatException("表达式缺少右括号");
                }

                _pos++;
                return value;
            }

            if (current == '$')
            {
                return ReadPathValue();
            }

            if (char.IsDigit(current))
            {
                return ReadNumber();
            }

            throw new FormatException($"表达式在位置 {_pos} 出现无法识别的字符「{current}」");
        }

        /// <summary>读取 $ 路径并在 JSON 根上取值：$.a.b[0].c 或 $data.a.b（magpie 兼容）。</summary>
        private double ReadPathValue()
        {
            _pos++; // 跳过 $
            if (_pos + 3 < _text.Length
                && _text[_pos] == 'd' && _text[_pos + 1] == 'a' && _text[_pos + 2] == 't' && _text[_pos + 3] == 'a')
            {
                _pos += 4; // $data 等价 $
            }

            var element = root;
            var consumedAny = false;
            while (_pos < _text.Length)
            {
                if (_text[_pos] == '.')
                {
                    _pos++;
                    var name = ReadFieldName();
                    if (name.Length == 0)
                    {
                        throw new FormatException("路径中「.」后缺少字段名");
                    }

                    if (element.ValueKind != JsonValueKind.Object
                        || !element.TryGetProperty(name, out element))
                    {
                        throw new FormatException($"路径 $.{name} 在响应中不存在或不是对象");
                    }

                    consumedAny = true;
                    continue;
                }

                if (_text[_pos] == '[')
                {
                    _pos++;
                    var indexText = new StringBuilder();
                    while (_pos < _text.Length && _text[_pos] != ']')
                    {
                        indexText.Append(_text[_pos]);
                        _pos++;
                    }

                    if (_pos >= _text.Length || !int.TryParse(indexText.ToString(), out var index) || index < 0)
                    {
                        throw new FormatException("路径中的数组下标无效");
                    }

                    _pos++; // ]
                    if (element.ValueKind != JsonValueKind.Array || index >= element.GetArrayLength())
                    {
                        throw new FormatException($"路径下标 [{index}] 超出数组范围或目标不是数组");
                    }

                    element = element[index];
                    consumedAny = true;
                    continue;
                }

                break;
            }

            if (!consumedAny && TryReadNumber(element, out var whole))
            {
                return whole; // 裸 $ = 根数值
            }

            if (!TryReadNumber(element, out var value))
            {
                throw new FormatException("路径取到的值不是数值（也非数字字符串）");
            }

            return value;
        }

        private string ReadFieldName()
        {
            var builder = new StringBuilder();
            while (_pos < _text.Length
                   && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_' || _text[_pos] == '-'))
            {
                builder.Append(_text[_pos]);
                _pos++;
            }

            return builder.ToString();
        }

        private double ReadNumber()
        {
            var start = _pos;
            while (_pos < _text.Length
                   && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == 'e' || _text[_pos] == 'E'
                       || ((_text[_pos] == '+' || _text[_pos] == '-') && _pos > start
                           && (_text[_pos - 1] == 'e' || _text[_pos - 1] == 'E'))))
            {
                _pos++;
            }

            if (!double.TryParse(_text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"位置 {start} 起的数字无效");
            }

            return value;
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos < _text.Length)
            {
                throw new FormatException($"表达式在位置 {_pos} 之后还有未识别内容");
            }
        }
    }
}
