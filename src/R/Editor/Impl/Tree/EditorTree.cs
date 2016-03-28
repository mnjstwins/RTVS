﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Editor.Text;
using Microsoft.R.Core.AST;
using Microsoft.R.Core.AST.Definitions;
using Microsoft.R.Core.Parser;
using Microsoft.R.Core.Tokens;
using Microsoft.R.Editor.Tree.Definitions;
using Microsoft.VisualStudio.Text;

namespace Microsoft.R.Editor.Tree {
    /// <summary>
    /// Document tree for the editor. It does not derive from AST and rather
    /// aggregates it. The reason is that editor tree can be read and updated from
    /// different threads and hence we need to control access to the tree elements
    /// using appropriate locks. Typically there are three threads: main application
    /// thread which should be creating editor tree, incremental parse thread and
    /// validation (syntx check) thread.
    /// </summary>
    public partial class EditorTree : IEditorTree, IDisposable {
        
        #region IEditorTree
        /// <summary>
        /// Visual Studio core editor text buffer
        /// </summary>
        public ITextBuffer TextBuffer { get; private set; }

        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "AcquireReadLock")]
        [SuppressMessage("Microsoft.Design", "CA1065:DoNotRaiseExceptionsInUnexpectedLocations")]
        public AstRoot AstRoot {
            get {
                if (_ownerThread != Thread.CurrentThread.ManagedThreadId)
                    throw new ThreadStateException("Method should only be called on the main thread. Use AcquireReadLock when accessing tree from a background thread.");

                // Do not call EnsureTreeReady here since it may slow down
                // typing A LOT in large files. If some code needs up-to-date
                // tree it has to call EnsureTreeReady explicitly.
                return _astRoot;
            }
        }

        /// <summary>
        /// Previous AST. May be used in cases when parsing is not acceptable
        /// due to performance hit and full tree fidelity is not required
        /// such as in determining smart indent on Enter. 
        /// </summary>
        public AstRoot PreviousAstRoot { get; private set; }

        /// <summary>
        /// Returns true if tree matches current document snapshot
        /// </summary>
        public bool IsReady {
            get {
                if (IsDirty)
                    return false;

                if (TextBuffer != null && TextSnapshot != null) {
                    return TextBuffer.CurrentSnapshot.Version.VersionNumber == TextSnapshot.Version.VersionNumber;
                }

                return true;
            }
        }

        /// <summary>
        /// Last text snapshot associated with this tree
        /// </summary>
        public ITextSnapshot TextSnapshot {
            get {
                return _textSnapShot;
            }
            internal set {
                _textSnapShot = value;
                if (_astRoot != null) {
                    _astRoot.TextProvider = new TextProvider(_textSnapShot, partial: true);
                }
            }
        }

        /// <summary>
        /// Ensures tree is up to date, matches current text buffer snapshot
        /// and all changes since the last update were processed. Blocks until 
        /// all changes have been processed. Does not pump messages.
        /// </summary>
        public void EnsureTreeReady() {
            if (TreeUpdateTask == null)
                return;

            if (_ownerThread != Thread.CurrentThread.ManagedThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            // OK to run in sync if changes are pending since we need it updated now
            TreeUpdateTask.EnsureProcessingComplete();
        }

        /// <summary>
        /// Provides a way to automatically invoke particular action
        /// once when tree becomes ready again. Typically used in
        /// asynchronous completion and signature help scenarios.
        /// </summary>
        /// <param name="action">Action to invoke</param>
        /// <param name="p">Parameter to pass to the action</param>
        /// <param name="type">Action identifier</param>
        /// <param name="processNow">
        /// If true, change processing begins now. 
        /// If false, next regular parsing pass with process pending changes.
        /// </param>
        public void InvokeWhenReady(Action<object> action, object p, Type type, bool processNow = false) {
            if (IsReady) {
                action(p);
            } else {
                _actionsToInvokeOnReady[type] = new TreeReadyAction() { Action = action, Parameter = p };
                if(processNow) {
                    TreeUpdateTask.ProcessPendingTextBufferChanges(async: true);
                }
            }
        }
        #endregion

        /// <summary>
        /// True if tree is out of date and no longer matches current text buffer state
        /// </summary>
        public bool IsDirty {
            get { return TreeUpdateTask.ChangesPending; }
        }

        #region Internal members

        /// <summary>
        /// Makes current thread owner of the tree.
        /// Normally only one thread can access the tree.
        /// </summary>
        internal void TakeThreadOwnerShip() {
            _ownerThread = Thread.CurrentThread.ManagedThreadId;
            TreeUpdateTask.TakeThreadOwnership();
            TreeLock.TakeThreadOwnership();
        }

        internal AstRoot GetAstRootUnsafe() {
            return _astRoot;
        }

        /// <summary>
        /// Async tree update task
        /// </summary>
        internal TreeUpdateTask TreeUpdateTask { get; private set; }

        #endregion

        #region Private members
        /// <summary>
        /// Tree lock
        /// </summary>
        internal EditorTreeLock TreeLock { get; private set; }

        /// <summary>
        /// Tree owner thread id (usually main thread)
        /// </summary>
        int _ownerThread;

        /// <summary>
        /// Current text buffer snapshot
        /// </summary>
        ITextSnapshot _textSnapShot;

        /// <summary>
        /// Parse tree
        /// </summary>
        private AstRoot _astRoot;

        class TreeReadyAction {
            public Action<object> Action;
            public object Parameter;
        }

        private Dictionary<Type, TreeReadyAction> _actionsToInvokeOnReady = new Dictionary<Type, TreeReadyAction>();
        #endregion

        #region Constructors
        /// <summary>
        /// Creates document tree on a given text buffer.
        /// </summary>
        /// <param name="textBuffer">Text buffer</param>
        public EditorTree(ITextBuffer textBuffer) {
            _ownerThread = Thread.CurrentThread.ManagedThreadId;

            TextBuffer = textBuffer;
            TextBuffer.ChangedHighPriority += OnTextBufferChanged;

            TreeUpdateTask = new TreeUpdateTask(this);
            TreeLock = new EditorTreeLock();
        }
        #endregion

        #region Building
        /// <summary>
        /// Builds initial AST. Subsequent updates should be coming from a background thread.
        /// </summary>
        public void Build() {
            if (_ownerThread != Thread.CurrentThread.ManagedThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            var sw = Stopwatch.StartNew();

            TreeUpdateTask.Cancel();

            if (TextBuffer != null) {
                TextSnapshot = TextBuffer.CurrentSnapshot;
                _astRoot = RParser.Parse(new TextProvider(TextBuffer.CurrentSnapshot));
            }

            TreeUpdateTask.ClearChanges();

            // Fire UpdatesPending notification, even though we don't have ranges for the event
            List<TextChangeEventArgs> textChanges = new List<TextChangeEventArgs>();
            FireOnUpdatesPending(textChanges);

            FireOnUpdateBegin();
            FireOnUpdateCompleted(TreeUpdateType.NewTree);

            sw.Stop();
        }

        /// <summary>
        /// Initiates processing of pending changes synchronously.
        /// </summary>
        internal void ProcessChanges() {
            if (this.IsDirty) {
                TreeUpdateTask.ProcessPendingTextBufferChanges(false);
            }
        }
        #endregion

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e) {
            if (e.Changes.Count > 0) {
                // In case of tabbing multiple lines update comes as multiple changes
                // each is an insertion of whitespace in the beginning of the line.
                // We don't want to combine them since then change will technically
                // damage existing elements while actually it is just a whitespace change.
                // All changes are relative to the current snapshot hence we have to transform 
                // them first and make them relative to each other so we can apply changes
                // sequentially as after every change element positions will shift and hence
                // next change must be relative to the new position and not to the current
                // text buffer snapshot. Changes are sorted by position.
                List<TextChangeEventArgs> textChanges = TextUtility.ConvertToRelative(e);
                TreeUpdateTask.OnTextChanges(textChanges);
            }
        }

        internal void NotifyTextChange(int start, int oldLength, int newLength) {
            TextChangeEventArgs change = new TextChangeEventArgs(start, start, oldLength, newLength);
            List<TextChangeEventArgs> changes = new List<TextChangeEventArgs>(1);
            changes.Add(change);

            _astRoot.ReflectTextChanges(changes);
        }

        internal TextChange PendingChanges {
            get { return TreeUpdateTask.Changes; }
        }

        /// <summary>
        /// Removes nodes from the tree collection if node range is 
        /// // partially or entirely within the deleted region.
        /// This is needed since parsing is asynchronous and without
        /// removing damaged nodes so intellisense will still be able
        /// to find them in the tree which actually they are gone.
        /// Returns true if full parse required.
        /// </summary>
        /// <param name="node">Node to start from</param>
        /// <param name="range">Range to invalidate elements in</param>
        internal bool InvalidateInRange(IAstNode node, ITextRange range, out bool nodesChanged) {
            var removedElements = new List<IAstNode>();
            int firstToRemove = -1;
            int lastToRemove = -1;

            nodesChanged = false;

            for (int i = 0; i < node.Children.Count; i++) {
                var child = node.Children[i];
                bool removeChild = false;

                if (range.Start == child.Start && range.Length == 0) {
                    // Change is right before the node
                    break;
                }

                if (!removeChild && TextRange.Intersect(range, child)) {
                    bool childElementsChanged;
                    if (child is TokenNode) {
                        childElementsChanged = true;
                    } else {
                        InvalidateInRange(child, range, out childElementsChanged);
                    }

                    nodesChanged |= childElementsChanged;

                    // Check if we really to remove this node. Damaged children were already 
                    // removed and if node itself is not damaged, keep it.
                    if (TextRange.Contains(child, range)) {
                        // Fully contained like when small change inside { } scope.
                        IAstNode startNode, endNode;
                        PositionType startPositionType, endPostionType;
                        var n = child.GetElementsEnclosingRange(range.Start, range.Length, out startNode, out startPositionType, out endNode, out endPostionType);
                        if (!TextRange.Contains(n, range)) {
                            removeChild = true;
                        }
                    } else {
                        removeChild = true;
                    }
                }

                if (removeChild) {
                    if (firstToRemove < 0)
                        firstToRemove = i;

                    lastToRemove = i;
                }
            }

            if (firstToRemove >= 0) {
                for (int i = firstToRemove; i <= lastToRemove; i++) {
                    IAstNode child = node.Children[i];
                    removedElements.Add(child);

                    _astRoot.Errors.RemoveInRange(child);
                }

                node.RemoveChildren(firstToRemove, lastToRemove - firstToRemove + 1);
            }

            if (removedElements.Count > 0) {
                nodesChanged = true;
                FireOnNodesRemoved(removedElements);
            }

            return nodesChanged;
        }

        /// <summary>
        /// Removes all elements from the tree
        /// </summary>
        /// <returns>Number of removed elements</returns>
        public int Invalidate() {
            // make sure not to use RootNode property since
            // calling get; causes parse
            List<IAstNode> removedNodes = new List<IAstNode>();
            foreach (var child in _astRoot.Children) {
                removedNodes.Add(child);
            }

            if (_astRoot.Children.Count > 0) {
                PreviousAstRoot = _astRoot;
            }
            _astRoot = new AstRoot(_astRoot.TextProvider);

            if (removedNodes.Count > 0) {
                FireOnNodesRemoved(removedNodes);
            }

            return removedNodes.Count;
        }

        #region Tree Access
        public AstRoot AcquireReadLock(Guid treeUserId) {
            if (TreeLock != null) {
                if (TreeLock.AcquireReadLock(treeUserId)) {
                    return _astRoot;
                }

                Debug.Fail(String.Format(CultureInfo.CurrentCulture, "Unable to acquire read lock for user {0}", treeUserId));
            }

            return null;
        }

        public bool ReleaseReadLock(Guid treeUserId) {
            return TreeLock.ReleaseReadLock(treeUserId);
        }

        public bool AcquireWriteLock() {
            return TreeLock.AcquireWriteLock();
        }

        public bool ReleaseWriteLock() {
            return TreeLock.ReleaseWriteLock();
        }
        #endregion

        /// <summary>
        /// Determines position type and enclosing element node for a given position in the document text.
        /// </summary>
        /// <param name="position">Position in the document text</param>
        /// <param name="node">Node that contains position</param>
        /// <returns>Position type as a set of flags combined via OR operation</returns>
        public PositionType GetPositionElement(int position, out IAstNode node) {
            return this.AstRoot.GetPositionNode(position, out node);
        }

        #region Comments
        /// <summary>
        /// Determines if a given range is inside a comment and 
        /// returns range of the comment block.
        /// </summary>
        /// <param name="range">Text range to check</param>
        /// <returns>Text range of the found comment block or empty range if not found.</returns>
        public ITextRange GetCommentBlockContainingRange(ITextRange range) {
            TextRangeCollection<RToken> comments = this.AstRoot.Comments;

            var commentsInRange = comments.ItemsInRange(range);
            if (commentsInRange.Count == 1)
                return commentsInRange[0];

            return TextRange.EmptyRange;
        }

        /// <summary>
        /// Determines if a given position is inside a comment 
        /// and returns range of the comment block.
        /// </summary>
        /// <param name="position">Position to check</param>
        /// <returns>Outer range of the found comment block or empty range if not found.</returns>
        public ITextRange GetCommentBlockFromPosition(int position) {
            return GetCommentBlockContainingRange(new TextRange(position, 0));
        }

        /// <summary>
        /// Determines if a given range is inside a comment.
        /// </summary>
        /// <param name="range">Text range</param>
        /// <returns>True if range is entirely inside a comment block</returns>
        public bool IsRangeInComment(ITextRange range) {
            var block = this.GetCommentBlockContainingRange(range);
            return block.Length > 0;
        }

        /// <summary>
        /// Determines if a given position is inside a comment of any kind.
        /// Comment may be plain HTML comment or artifact type comment
        /// like &lt;%--...--%> in ASP.NET or @* ... *@ in Razor.
        /// </summary>
        /// <param name="position">Position in the document</param>
        /// <returns>True if position is inside a comment block</returns>
        public bool IsRangeInComment(int position) {
            return IsRangeInComment(new TextRange(position, 0));
        }
        #endregion

        #region Dispose
        [ExcludeFromCodeCoverage]
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                if (TextBuffer != null) {
                    if (Closing != null)
                        Closing(this, null);

                    if (TreeUpdateTask != null) {
                        TreeUpdateTask.Dispose();
                        TreeUpdateTask = null;
                    }

                    if (TreeLock != null) {
                        TreeLock.Dispose();
                        TreeLock = null;
                    }

                    TextBuffer.ChangedHighPriority -= OnTextBufferChanged;
                    TextBuffer = null;
                }
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

        [ExcludeFromCodeCoverage]
        public override string ToString() {
            return String.Format(CultureInfo.CurrentCulture, "IsDirty: {0} {1} Changes: {2}", IsDirty, TreeLock.ToString(), TreeUpdateTask.ChangesPending);
        }
    }
}
