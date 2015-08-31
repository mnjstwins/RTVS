﻿using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Languager.Editor.Application;
using Microsoft.Languages.Editor.Application.Composition;
using Microsoft.Languages.Editor.Application.Controller;
using Microsoft.Languages.Editor.Application.Host;
using Microsoft.Languages.Editor.Controller;
using Microsoft.Languages.Editor.EditorFactory;
using Microsoft.Languages.Editor.Shell;
using Microsoft.Languages.Editor.Test.Composition;
using Microsoft.Languages.Editor.Tests.Shell;
using Microsoft.Languages.Editor.Workspace;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.Languages.Editor.Application.Core
{
    public class CoreEditor
    {
        public ICommandTarget BaseController { get; private set; }

        [Import]
        IContentTypeRegistryService ContentTypeRegistryService { get; set; }

        [Import]
        ITextEditorFactoryService TextEditorFactoryService { get; set; }

        [Import]
        ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        [Import]
        ITextBufferFactoryService TextBufferFactoryService { get; set; }

        [Import]
        IClassificationFormatMapService ClassificationFormatMapService { get; set; }

        [Import]
        ITextBufferUndoManagerProvider TextBufferUndoManagerProvider { get; set; }

        [Import]
        IEditorOperationsFactoryService EditorOperationsFactoryService { get; set; }

        [Import]
        IEditorOptionsFactoryService EditorOptionsFactoryService { get; set; }

        static private object _lock = new object();
        private static IEditorShell _editorShell;

        private IWpfTextViewHost _wpftextViewHost;
        private IContentType _contentType;
        private ITextBufferUndoManager _undoManager;
        private IEditorOperations _editorOperations;
        private string _filePath;
        private ICompositionService _compositionService;
        private IEditorInstance _editorIntance;
        private IWorkspaceItem _workspaceItem;

        public CoreEditor(string text, string filePath, string contentTypeName)
        {
            _compositionService = TestCompositionCatalog.CompositionService;

            if (_editorShell == null)
            {
                _editorShell = TestEditorShell.Create();
                EditorShell.SetShell(_editorShell);

                EditorShell.UIThread = Thread.CurrentThread;
            }

            _compositionService.SatisfyImportsOnce(this);
            _filePath = filePath;

            if (String.IsNullOrEmpty(_filePath))
            {
                if (contentTypeName == null)
                    throw new ArgumentNullException("contentTypeName");

                _contentType = ContentTypeRegistryService.GetContentType(contentTypeName);
            }

            CreateTextViewHost(text, filePath);
        }

        #region Properties

        public IWpfTextView View
        {
            get
            {
                if (_wpftextViewHost != null)
                    return _wpftextViewHost.TextView;

                return null;
            }
        }

        public ITextBufferUndoManager UndoManager
        {
            get
            {
                if (_undoManager == null)
                    _undoManager = TextBufferUndoManagerProvider.GetTextBufferUndoManager(TextBuffer);

                return _undoManager;
            }
        }

        public IEditorOperations EditorOperations
        {
            get
            {
                if (_editorOperations == null)
                    _editorOperations = EditorOperationsFactoryService.GetEditorOperations(View);

                return _editorOperations;
            }
        }

        public IEditorOptions EditorOptions
        { get { return EditorOptionsFactoryService.GetOptions(View); } }


        private IEditorOptions EditorOptionsForTabs
        { get { return EditorOptionsFactoryService.GetOptions(TextBuffer); } }


        private IEditorOptions GlobalOptions
        { get { return EditorOptionsFactoryService.GlobalOptions; } }

        /// <summary>
        /// Calling TextBuffer is only valid after instantiating the View
        /// </summary>
        private ITextBuffer TextBuffer
        {
            get
            {
                Debug.Assert(_wpftextViewHost != null, "View was not created yet");

                if (_wpftextViewHost.TextView != null)
                    return _wpftextViewHost.TextView.TextBuffer;

                return null;
            }
        }

        private IContentType ContentType
        {
            get
            {
                if (_contentType == null)
                {
                    var ctl = new ContentTypeLocator(_compositionService);
                    _contentType = ctl.FindContentType(_filePath);
                }

                return _contentType;
            }
        }

        #endregion

        private void CreateTextViewHost(string text, string filePath)
        {
            if (text == null)
                text = string.Empty;

            var diskBuffer = TextBufferFactoryService.CreateTextBuffer(text, ContentType);

            _workspaceItem = new WorkspaceItem(filePath, filePath);
            _editorIntance = EditorInstanceFactory.CreateEditorInstance(_workspaceItem, diskBuffer, _compositionService);

            ITextDataModel textDataModel;

            if (_editorIntance != null)
            {
                textDataModel = new TextDataModel(diskBuffer, _editorIntance.ViewBuffer);
            }
            else
            {
                textDataModel = new TextDataModel(diskBuffer, diskBuffer);
            }

            var textBuffer = textDataModel.DocumentBuffer;
            TextDocument = TextDocumentFactoryService.CreateTextDocument(textBuffer, filePath);

            var textView = TextEditorFactoryService.CreateTextView(textDataModel,
                                                                   new DefaultTextViewRoleSet(),
                                                                   GlobalOptions);

            _wpftextViewHost = TextEditorFactoryService.CreateTextViewHost(textView, true);

            ApplyDefaultSettings();

            _contentControl.Content = _wpftextViewHost.HostControl;

            var baseController = new BaseController();
            BaseController = baseController;

            if (_editorIntance != null)
            {
                CommandTarget = _editorIntance.GetCommandTarget(textView);
                var controller = CommandTarget as Microsoft.Languages.Editor.Controller.Controller;
                controller.ChainedController = baseController;
            }
            else
            {
                CommandTarget = baseController;
            }

            baseController.Initialize(textView, EditorOperations, UndoManager);
        }

        public void Close()
        {
            if (_wpftextViewHost != null)
            {
                _wpftextViewHost.Close();
                _wpftextViewHost = null;
            }

            if (_editorIntance != null)
            {
                _editorIntance.Dispose();
                _editorIntance = null;
            }
        }

        private void ApplyDefaultSettings()
        {
            EditorOptions.SetOptionValue<bool>(DefaultTextViewOptions.UseVisibleWhitespaceId, true);

            EditorOptions.SetOptionValue<int>(DefaultOptions.TabSizeOptionId, 4);
            EditorOptions.SetOptionValue<int>(DefaultOptions.IndentSizeOptionId, 4);
            EditorOptions.SetOptionValue<bool>(DefaultTextViewHostOptions.LineNumberMarginId, true);
            EditorOptions.SetOptionValue<bool>(DefaultTextViewHostOptions.OutliningMarginId, true);

            var textFormatMap = ClassificationFormatMapService.GetClassificationFormatMap("text");
            textFormatMap.DefaultTextProperties = textFormatMap.DefaultTextProperties.SetFontRenderingEmSize(11);
            textFormatMap.DefaultTextProperties = textFormatMap.DefaultTextProperties.SetTypeface(new Typeface("Consolas"));
        }

        public bool HasFocus
        {
            get
            {
                if (View != null && View.VisualElement != null && Control != null)
                {
                    return View.VisualElement.IsKeyboardFocusWithin;
                }
                else
                {
                    return false;
                }
            }
        }

        private ContentControl _contentControl = new ContentControl();
        public Control Control
        { get { return _contentControl; } }

        public ICommandTarget CommandTarget
        { get; private set; }

        public void Focus()
        {
            if (View != null && View.VisualElement != null)
            {
                if (!View.VisualElement.Focus())
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(
                        (Action)(() =>
                        {
                            if (View != null && View.VisualElement != null)
                            {
                                View.VisualElement.Focus();
                            }
                        })
                        , DispatcherPriority.ApplicationIdle,
                        null);
                }
            }
        }

        public bool Dirty
        { get { return (TextDocument != null) ? TextDocument.IsDirty : false; } }

        public ITextDocument TextDocument { get; private set; }

        public string Text
        {
            get
            {
                return TextBuffer.CurrentSnapshot.GetText();
            }
            set
            {
                string text = value;

                if (text == null)
                {
                    text = string.Empty;
                }

                TextBuffer.Replace(new Span(0, TextBuffer.CurrentSnapshot.Length), text);
            }
        }

        public int CurrentColumn
        {
            get
            {
                ITextViewLine caretViewLine = View.Caret.ContainingTextViewLine;
                double columnWidth = View.FormattedLineSource.ColumnWidth;
                return (int)Math.Round((View.Caret.Left - caretViewLine.Left) / columnWidth);
            }
        }

        public int CurrentLine
        {
            get
            {
                int caretIndex = View.Caret.Position.VirtualBufferPosition.Position;
                return View.TextSnapshot.GetLineNumberFromPosition(caretIndex);
            }
        }

        public void GoTo(int line, int offsetFromStart, int length)
        {
            var snapshot = View.TextSnapshot;

            if (line >= snapshot.LineCount || line < 0)
            {
                Debug.Fail("Line number must be between 0 and linecount");
                return;
            }

            var snapshotLine = View.TextSnapshot.GetLineFromLineNumber(line);

            if (offsetFromStart > 0 && (offsetFromStart > snapshotLine.LengthIncludingLineBreak || offsetFromStart < 0))
            {
                Debug.Fail("Offset must be between 0 and chars in line");
                return;
            }

            var bufferIntPosition = snapshotLine.Start.Position + offsetFromStart;

            var bufferPosition = new SnapshotPoint(snapshot, bufferIntPosition);

            View.Caret.MoveTo(bufferPosition);

            Debug.Assert(length >= 0, "Length must be 0 or more");

            if (length > 0)
            {
                SnapshotSpan selectionSpan = new SnapshotSpan(bufferPosition, length);

                View.Selection.Select(selectionSpan, false);
            }

            Focus();
        }

        public int LineCount
        { get { return TextBuffer.CurrentSnapshot.LineCount; } }


        public void SelectAll()
        { EditorOperations.SelectAll(); }

        public string SelectedText
        { get { return EditorOperations.SelectedText; } }
    }
}