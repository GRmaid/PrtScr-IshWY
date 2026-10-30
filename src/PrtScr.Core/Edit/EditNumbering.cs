namespace PrtScr.Core.Edit;

/// <summary>序号工具的纯规则。</summary>
public static class EditNumbering
{
    /// <summary>
    /// 下一个序号：没有正数序号时返回 1，否则返回现有最大序号 + 1。
    /// 撤销最后一条序号标注后，被删掉的尾号会自动可复用。
    /// </summary>
    public static int NextAfter(IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);

        var max = 0;
        for (var i = 0; i < numbers.Count; i++)
        {
            if (numbers[i] > max)
                max = numbers[i];
        }

        return max + 1;
    }
}
