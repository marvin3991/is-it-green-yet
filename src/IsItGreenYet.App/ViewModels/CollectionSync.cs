using System.Collections.ObjectModel;

namespace IsItGreenYet.App.ViewModels;

/// <summary>
/// 把 ObservableCollection 原地改成目標順序：只做必要的移除、移動與插入，不整份重建（spec §10.10 差異更新）。
/// 沿用的實例保持不動，清單的選取、捲動位置與正在顯示的 tooltip 都不受影響。
/// </summary>
internal static class CollectionSync
{
    /// <param name="target">目標順序；同一個實例不得出現兩次（呼叫端先去重）。</param>
    /// <returns>這次新插入的項目。</returns>
    public static IReadOnlyList<T> Apply<T>(ObservableCollection<T> collection, IReadOnlyList<T> target)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(target);

        var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(collection[index]))
            {
                collection.RemoveAt(index);
            }
        }

        var inserted = new List<T>();
        for (var index = 0; index < target.Count; index++)
        {
            var item = target[index];
            if (index < collection.Count && ReferenceEquals(collection[index], item))
            {
                continue;
            }

            var current = IndexOf(collection, item, index + 1);
            if (current >= 0)
            {
                collection.Move(current, index);
            }
            else
            {
                collection.Insert(index, item);
                inserted.Add(item);
            }
        }

        return inserted;
    }

    /// <summary>順序相同而且是同一批實例。</summary>
    public static bool SameSequence<T>(IReadOnlyList<T> collection, IReadOnlyList<T> target)
        where T : class
    {
        if (collection.Count != target.Count)
        {
            return false;
        }

        for (var index = 0; index < target.Count; index++)
        {
            if (!ReferenceEquals(collection[index], target[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf<T>(ObservableCollection<T> collection, T item, int start)
        where T : class
    {
        for (var index = start; index < collection.Count; index++)
        {
            if (ReferenceEquals(collection[index], item))
            {
                return index;
            }
        }

        return -1;
    }
}
