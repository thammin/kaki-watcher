using System;
using System.Collections.Generic;

namespace Kaki.Watcher
{
    public struct WatcherOption
    {
        public bool immediate;
        public bool lazy;
    }

    public class Watcher<T> : IWatcher
    {
        public HashSet<IWatcher> Deps { get; set; }
        public HashSet<IWatcher> Subs { get; set; }

        Func<T> _getter;
        bool _dirty;
        bool _lazy;
        T _value;
        Action<T, T> _cb;

        public Watcher(Func<T> getter, Action<T, T> cb = null, WatcherOption option = default)
        {
            Deps = new HashSet<IWatcher>();
            Subs = new HashSet<IWatcher>();

            _getter = getter;
            _lazy = option.lazy;
            _dirty = _lazy;
            _cb = cb;

            if (option.immediate)
            {
                Get();
            }
        }

        public void Update()
        {
            if (_lazy)
            {
                _dirty = true;
            }
            else
            {
                Get();
            }
        }

        public T Get()
        {
            WatcherStack.Push(this);

            var newValue = !_lazy || _dirty ? _getter() : _value;
            var oldValue = _value;
            _value = newValue;
            _dirty = false;

            _cb?.Invoke(newValue, oldValue);

            WatcherStack.Pop();
            CollectDeps();

            return newValue;
        }

        void CollectDeps()
        {
            if (WatcherStack.Current == null) return;

            foreach (var dep in Deps)
            {
                dep.Subs.Add(WatcherStack.Current);
                WatcherStack.Current.Deps.Add(dep);
            }

            Subs.Add(WatcherStack.Current);
            WatcherStack.Current.Deps.Add(this);
        }

        public void NotifyDeps()
        {
            foreach (var sub in Subs) sub.Update();
        }
    }

    public class WatcherStack
    {
        public static IWatcher Current;
        static Stack<IWatcher> Stacks = new Stack<IWatcher>();

        public static void Push(IWatcher watcher)
        {
            Stacks.Push(watcher);
            Current = watcher;
        }

        public static void Pop()
        {
            Stacks.Pop();
            Current = Stacks.Count != 0 ? Stacks.Peek() : null;
        }
    }

    public interface IWatcher
    {
        HashSet<IWatcher> Deps { get; set; }
        HashSet<IWatcher> Subs { get; set; }

        void Update();
    }

}
