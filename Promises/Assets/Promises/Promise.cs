﻿using System;
using System.Collections;
using System.Threading;

namespace Promises {
    public enum PromiseState : byte {
        Unfulfilled,
        Failed,
        Fulfilled
    }

    public static class Promise {
        public static Promise<T> WithAction<T>(Func<T> action) {
            var deferred = new Deferred<T>();
            deferred.action = action;
            return deferred.RunAsync();
        }

        public static Promise<T> WithCoroutine<T>(Func<IEnumerator> coroutine) {
            var deferred = new Deferred<T>();
            deferred.coroutine = coroutine;
            return deferred.RunAsync();
        }

        public static Promise<object[]> All(params Promise<object>[] promises) {
            var deferred = new Deferred<object[]>();
            var results = new object[promises.Length];
            var done = 0;

            var initialProgress = 0f;
            foreach (var p in promises) {
                initialProgress += p.progress;
            }

            deferred.Progress(initialProgress / (float)promises.Length);

            for (int i = 0, promisesLength = promises.Length; i < promisesLength; i++) {
                var localIndex = i;
                var promise = promises[localIndex];
                promise.OnFulfilled += result => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        results[localIndex] = result;
                        Interlocked.Increment(ref done);
                        if (done == promisesLength) {
                            deferred.Fulfill(results);
                        }
                    }
                };
                promise.OnFailed += error => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        deferred.Fail(error);
                    }
                };
                promise.OnProgressed += progress => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        var totalProgress = 0f;
                        foreach (var p in promises) {
                            totalProgress += p.progress;
                        }
                        deferred.Progress(totalProgress / (float)promisesLength);
                    }
                };
            }

            return deferred.promise;
        }

        public static Promise<T> Any<T>(params Promise<T>[] promises) {
            var deferred = new Deferred<T>();
            var failed = 0;

            var initialProgress = 0f;
            foreach (var p in promises) {
                if (p.progress > initialProgress) {
                    initialProgress = p.progress;
                }
            }
            deferred.Progress(initialProgress);

            for (int i = 0, promisesLength = promises.Length; i < promisesLength; i++) {
                var localIndex = i;
                var promise = promises[localIndex];
                promise.OnFulfilled += result => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        deferred.Fulfill(result);
                    }
                };
                promise.OnFailed += error => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        Interlocked.Increment(ref failed);
                        if (failed == promisesLength) {
                            deferred.Fail(new PromiseAnyException());
                        }
                    }
                };
                promise.OnProgressed += progress => {
                    if (deferred.state == PromiseState.Unfulfilled) {
                        var maxProgress = 0f;
                        foreach (var p in promises) {
                            if (p.progress > maxProgress) {
                                maxProgress = p.progress;
                            }
                        }
                        deferred.Progress(maxProgress);
                    }
                };
            }

            return deferred.promise;
        }

        public static Promise<object[]> Collect(params Promise<object>[] promises) {
            var deferred = new Deferred<object[]>();
            var results = new object[promises.Length];
            var done = 0;

            var initialProgress = 0f;
            foreach (var p in promises) {
                initialProgress += p.progress;
            }

            deferred.Progress(initialProgress / (float)promises.Length);

            for (int i = 0, promisesLength = promises.Length; i < promisesLength; i++) {
                var localIndex = i;
                var promise = promises[localIndex];
                promise.OnFulfilled += result => {
                    results[localIndex] = result;
                    Interlocked.Increment(ref done);
                    if (done == promisesLength) {
                        deferred.Fulfill(results);
                    }
                };
                promise.OnFailed += error => {
                    var totalProgress = 0f;
                    foreach (var p in promises) {
                        totalProgress += p.state == PromiseState.Failed ? 1f : p.progress;
                    }
                    deferred.Progress(totalProgress / (float)promisesLength);
                    Interlocked.Increment(ref done);
                    if (done == promisesLength) {
                        deferred.Fulfill(results);
                    }
                };
                promise.OnProgressed += progress => {
                    var totalProgress = 0f;
                    foreach (var p in promises) {
                        totalProgress += p.state == PromiseState.Failed ? 1f : p.progress;
                    }
                    deferred.Progress(totalProgress / (float)promisesLength);
                };
            }

            return deferred.promise;
        }
    }

    public class State<T> {
        public PromiseState state { get { return _state; } }
        public T result { get { return _result; } }
        public Exception error { get { return _error; } }
        public float progress { get { return _progress; } }
        public Thread thread { get { return _thread; } }
        public bool allDelegatesCalled { get { return _allDelegatesCalled; } }

        readonly PromiseState _state;
        readonly T _result;
        readonly Exception _error;
        readonly float _progress;
        readonly Thread _thread;
        readonly bool _allDelegatesCalled;

        State(PromiseState state, T result, Exception error, float progress, Thread thread, bool allDelegatesCalled) {
            _state = state;
            _result = result;
            _error = error;
            _progress = progress;
            _thread = thread;
            _allDelegatesCalled = allDelegatesCalled;
        }

        public static State<T> CreateUnfulfilled() {
            return new State<T>(PromiseState.Unfulfilled, default(T), null, 0f, null, false);
        }

        public State<T> SetFulfilled(T result) {
            return new State<T>(PromiseState.Fulfilled, result, null, 1f, null, false);
        }

        public State<T> SetFailed(Exception error) {
            return new State<T>(PromiseState.Failed, _result, error, _progress, null, false);
        }

        public State<T> SetProgress(float p) {
            return new State<T>(_state, _result, _error, p, _thread, false);
        }

        public State<T> SetThread(Thread t) {
            return new State<T>(_state, _result, _error, _progress, t, false);
        }

        public State<T> SetAllDelegatesCalled() {
            return new State<T>(_state, _result, _error, _progress, _thread, true);
        }
    }

    public class Promise<T> {
        public event Fulfilled OnFulfilled {
            add { addOnFulfilled(value); }
            remove { _onFulfilled -= value; }
        }

        public event Failed OnFailed {
            add { addOnFailed(value); }
            remove { _onFailed -= value; }
        }

        public event Progressed OnProgressed {
            add { addOnProgress(value); }
            remove { _onProgressed -= value; }
        }

        public delegate void Fulfilled(T result);
        public delegate void Failed(Exception error);
        public delegate void Progressed(float progress);

        public PromiseState state { get { return _state.state; } }
        public T result { get { return _state.result; } }
        public Exception error { get { return _state.error; } }
        public float progress { get { return _state.progress; } }
        public Thread thread { get { return _state.thread; } }

        event Fulfilled _onFulfilled;
        event Failed _onFailed;
        event Progressed _onProgressed;

        protected volatile State<T> _state;
        readonly object _lock = new object();

        int _depth = 1;
        float _bias = 0f;
        float _fraction = 1f;

        public Promise() {
            _state = State<T>.CreateUnfulfilled();
        }

        public void Await() {
            while (state == PromiseState.Unfulfilled || !_state.allDelegatesCalled);
        }

        public Promise<TThen> Then<TThen>(Func<T, TThen> action) {
            var deferred = new Deferred<TThen>();
            deferred.action = () => action(result);
            return Then(deferred.promise);
        }

        public Promise<TThen> ThenCoroutine<TThen>(Func<T, IEnumerator> coroutine) {
            var deferred = new Deferred<TThen>();
            deferred.coroutine = () => coroutine(result);
            return Then(deferred.promise);
        }

        public Promise<TThen> Then<TThen>(Promise<TThen> promise) {
            var deferred = (Deferred<TThen>)promise;
            deferred._depth = _depth + 1;
            deferred._fraction = 1f / deferred._depth;
            deferred._bias = (float)_depth / (float)deferred._depth * progress;
            deferred.Progress(0);

            // Unity workaround. For unknown reasons, Unity won't compile using OnFulfilled += ..., OnFailed += ... or OnProgressed += ...
            addOnFulfilled(result => deferred.RunAsync());
            addOnFailed(deferred.Fail);
            addOnProgress(p => {
                deferred._bias = (float)_depth / (float)deferred._depth * p;
                deferred.Progress(0);
            });
            return deferred.promise;
        }

        public Promise<T> Rescue(Func<Exception, T> action) {
            var deferred = createDeferredRescue();
            deferred.action = () => action(error);
            return deferred.promise;
        }

        public Promise<T> RescueCoroutine(Func<Exception, IEnumerator> coroutine) {
            var deferred = createDeferredRescue();
            deferred.coroutine = () => coroutine(error);
            return deferred.promise;
        }

        Deferred<T> createDeferredRescue() {
            var deferred = new Deferred<T>();
            deferred._depth = _depth;
            deferred._fraction = 1f;
            deferred.Progress(progress);
            addOnFulfilled(deferred.Fulfill);
            addOnFailed(error => deferred.RunAsync());
            addOnProgress(deferred.Progress);
            return deferred;
        }

        public Promise<TWrap> Wrap<TWrap>() {
            var deferred = new Deferred<TWrap>();
            deferred._depth = _depth;
            deferred._fraction = 1f;
            deferred.Progress(progress);
            addOnFulfilled(result => deferred.Fulfill((TWrap)(object)result));
            addOnFailed(deferred.Fail);
            addOnProgress(deferred.Progress);
            return deferred.promise;
        }

        public override string ToString() {
            if (state == PromiseState.Fulfilled) {
                return string.Format("[Promise<{0}>: state = {1}, result = {2}]", typeof(T).Name, state, result);
            }
            if (state == PromiseState.Failed) {
                return string.Format("[Promise<{0}>: state = {1}, progress = {2:0.###}, error = {3}]", typeof(T).Name, state, progress, error.Message);
            }

            return string.Format("[Promise<{0}>: state = {1}, progress = {2:0.###}]", typeof(T).Name, state, progress);
        }

        void addOnFulfilled(Fulfilled value) {
            lock (_lock) {
                if (state == PromiseState.Unfulfilled) {
                    _onFulfilled += value;
                } else if (state == PromiseState.Fulfilled) {
                    value(result);
                }
            }
        }

        void addOnFailed(Failed value) {
            lock (_lock) {
                if (state == PromiseState.Unfulfilled) {
                    _onFailed += value;
                } else if (state == PromiseState.Failed) {
                    value(error);
                }
            }
        }

        void addOnProgress(Progressed value) {
            lock (_lock) {
                if (progress < 1f) {
                    _onProgressed += value;
                } else {
                    value(progress);
                }
            }
        }

        protected void transitionToFulfilled(T result) {
            lock (_lock) {
                if (state == PromiseState.Unfulfilled) {
                    var oldProgress = progress;
                    _state = _state.SetFulfilled(result);
                    if (Math.Abs(progress - oldProgress) > float.Epsilon) {
                        if (_onProgressed != null) {
                            _onProgressed(progress);
                        }
                    }
                    if (_onFulfilled != null) {
                        _onFulfilled(result);
                    }
                } else {
                    throw new Exception(string.Format("Invalid state transition from {0} to {1}", state, PromiseState.Fulfilled));
                }
            }

            cleanup();
        }

        protected void transitionToFailed(Exception error) {
            lock (_lock) {
                if (state == PromiseState.Unfulfilled) {
                    _state = _state.SetFailed(error);
                    if (_onFailed != null) {
                        _onFailed(error);
                    }
                } else {
                    throw new Exception(string.Format("Invalid state transition from {0} to {1}", state, PromiseState.Failed));
                }
            }
            
            cleanup();
        }

        protected void setProgress(float p) {
            lock (_lock) {
                var newProgress = _bias + p * _fraction;
                if (Math.Abs(newProgress - progress) > float.Epsilon) {
                    _state = _state.SetProgress(newProgress);
                    if (_onProgressed != null) {
                        _onProgressed(progress);
                    }
                }
            }
        }

        void cleanup() {
            _onFulfilled = null;
            _onFailed = null;
            _onProgressed = null;
            _state = _state.SetAllDelegatesCalled();
        }
    }
}

