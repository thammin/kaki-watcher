# Kaki.Watcher

A [Weaver](https://github.com/ByronMayne/Weaver) plugin to implement [Vue's internal observer](https://github.com/vuejs/vue/tree/dev/src/core/observer).

!! Experimental !!

### Example

```cs
using Kaki.Watcher;

public class Example : MonoBehaviour
{
    [Reactive]
    public int Number1 { get; set; } = 1;

    [Reactive]
    public int Number2 { get; set; } = 2;

    [Computed]
    public string Total
    {
        get
        {
            Debug.Log("lazy evaluated and cached!");
            return $"{Number1} + {Number2} = {Number1 + Number2}";
        }
    }

    public void Start()
    {
        Debug.Log(Total);
        // Log: "lazy evaluated and cached!"
        // Log: "1 + 2 = 3"

        Debug.Log(Total);
        // Log: "1 + 2 = 3"
        // Value was cached and returned without execute the getter method.

        Number1 = 5;
        // Try changing the reactive value
        // Still do not execute Total's getter method because it is lazy evulation

        Debug.Log(Total);
        // Log: "lazy evaluated and cached!"
        // Log: "5 + 2 = 7"
    }
}
```

# Install

via Package Manager UI

```sh
ssh://git@github.com/thammin/kaki-watcher.git
```

# License

MIT
