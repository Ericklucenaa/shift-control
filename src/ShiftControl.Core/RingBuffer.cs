namespace ShiftControl.Core;

/// <summary>Buffer circular de tamanho fixo. Não é thread-safe; quem compartilha usa um lock.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    /// <summary>Índice 0 é o item mais antigo.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length];
        }
    }

    public T Last => Count == 0 ? throw new InvalidOperationException("Buffer vazio.") : this[Count - 1];

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    /// <summary>Copia os itens (do mais antigo ao mais novo) para o fim de <paramref name="destination"/>.</summary>
    public int CopyTo(Span<T> destination)
    {
        int n = Math.Min(Count, destination.Length);
        int skip = Count - n;
        for (int i = 0; i < n; i++) destination[i] = this[skip + i];
        return n;
    }

    public T[] ToArray()
    {
        var result = new T[Count];
        CopyTo(result);
        return result;
    }
}
