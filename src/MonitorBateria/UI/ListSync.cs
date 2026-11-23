using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MonitorBateria.Models;

namespace MonitorBateria.UI;

internal static class ListSync
{
    /// <summary>
    /// Deixa <paramref name="target"/> igual a <paramref name="wanted"/> (mesma ordem) com o mínimo de
    /// alterações. Substitui ListCollectionView com filtro "ao vivo", que duplica itens quando a
    /// coleção de origem é reordenada.
    /// </summary>
    public static void Sync(ObservableCollection<BatteryDevice> target, IEnumerable<BatteryDevice> wanted)
    {
        var list = wanted.ToList();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!list.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int cur = target.IndexOf(list[i]);
            if (cur < 0) target.Insert(i, list[i]);
            else if (cur != i) target.Move(cur, i);
        }
    }
}
