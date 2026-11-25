using System.IO;
using System.Windows.Controls;
using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using Xunit;
using ListBox = System.Windows.Controls.ListBox;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class HandwritingMaterialSelectionBindingTests(WpfApplicationFixture wpf)
{
    [Fact]
    public async Task PendingSaveWithUnchangedSelectionAllowsPairedDeletion()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pim-handwriting-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalNoteStore(root);
            var note = await store.CreateAsync("手写删除选择");
            var withInk = await store.AddHandwritingAsync(
                note.NoteId,
                new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }),
                """{"schemaVersion":1,"width":320,"height":180,"strokes":[[[10,20,0.5],[30,40,0.5]]]}""",
                "鼠标手写",
                320,
                180,
                expectedCurrentVersionId: note.CurrentVersionId);
            var preview = withInk.Materials.Single(material =>
                material.Kind == LocalNoteMaterialKind.Handwriting);
            var source = withInk.Materials.Single(material =>
                material.Kind == LocalNoteMaterialKind.HandwritingSource);
            ListBox list = null!;
            HandwritingMaterialSelectionState selection = null!;
            MaterialSelectionBinding binding = null!;
            HandwritingMaterialSelectionFence fence = default;
            wpf.Run(() =>
            {
                list = new ListBox();
                selection = new HandwritingMaterialSelectionState();
                binding = new MaterialSelectionBinding(list, selection);
                binding.Bind(withInk.Materials);
                list.SelectedItem = withInk.Materials.Single(material =>
                    material.MaterialId == preview.MaterialId);
                fence = selection.Capture(preview.MaterialId);
            });

            var refreshed = await store.SaveAsync(note.NoteId, "已保存正文", "待保存正文");
            var refreshedPreview = refreshed.Materials.Single(material =>
                material.MaterialId == preview.MaterialId);
            Assert.NotSame(preview, refreshedPreview);

            wpf.Run(() =>
            {
                binding.Bind(refreshed.Materials);
                var selected = Assert.IsType<LocalNoteMaterial>(list.SelectedItem);
                Assert.Equal(preview.MaterialId, selected.MaterialId);
                Assert.True(selection.IsCurrent(fence, selected));
                Assert.True(binding.Select(preview.MaterialId));
                Assert.Equal(preview.MaterialId, binding.SelectedMaterial?.MaterialId);
            });

            var removed = await store.RemoveHandwritingAsync(
                note.NoteId,
                preview.MaterialId,
                expectedCurrentVersionId: refreshed.CurrentVersionId);
            Assert.DoesNotContain(
                removed.Materials,
                material => material.MaterialId == preview.MaterialId
                    || material.MaterialId == source.MaterialId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void PendingSaveWithUnchangedSelectionRebindsByMaterialId()
    {
        wpf.Run(() =>
        {
            var previewId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var originalMaterials = Materials(previewId, sourceId);
            var refreshedMaterials = Materials(previewId, sourceId);
            var list = new ListBox();
            var selection = new HandwritingMaterialSelectionState();
            var binding = new MaterialSelectionBinding(list, selection);
            binding.Bind(originalMaterials);
            list.SelectedItem = originalMaterials[0];
            var fence = selection.Capture(previewId);

            binding.Bind(refreshedMaterials);

            var selected = Assert.IsType<LocalNoteMaterial>(list.SelectedItem);
            Assert.Equal(previewId, selected.MaterialId);
            Assert.True(selection.IsCurrent(fence, selected));
            Assert.True(binding.Select(previewId));
            Assert.Equal(previewId, binding.SelectedMaterial?.MaterialId);
            Assert.Equal(
                sourceId,
                LocalInkMaterialPairing.ResolveSource(previewId, refreshedMaterials).MaterialId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingSaveWithClearedOrReselectedSelectionRejectsDeletion(bool reselect)
    {
        wpf.Run(() =>
        {
            var previewId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var originalMaterials = Materials(previewId, sourceId);
            var refreshedMaterials = Materials(previewId, sourceId);
            var list = new ListBox();
            var selection = new HandwritingMaterialSelectionState();
            var binding = new MaterialSelectionBinding(list, selection);
            binding.Bind(originalMaterials);
            list.SelectedItem = originalMaterials[0];
            var fence = selection.Capture(previewId);

            list.SelectedItem = null;
            if (reselect)
            {
                list.SelectedItem = originalMaterials[0];
            }

            binding.Bind(refreshedMaterials);

            Assert.Equal(
                reselect ? previewId : null,
                (list.SelectedItem as LocalNoteMaterial)?.MaterialId);
            Assert.False(selection.IsCurrent(fence, list.SelectedItem as LocalNoteMaterial));
        });
    }

    private static LocalNoteMaterial[] Materials(Guid previewId, Guid sourceId) =>
    [
        Material(previewId, LocalNoteMaterialKind.Handwriting, "手写.png"),
        Material(sourceId, LocalNoteMaterialKind.HandwritingSource, "手写.ink.json"),
    ];

    private static LocalNoteMaterial Material(
        Guid materialId,
        LocalNoteMaterialKind kind,
        string displayName) =>
        new()
        {
            MaterialId = materialId,
            Kind = kind,
            DisplayName = displayName,
            ContentSha256 = new string('0', 64),
            RelativePath = "materials/" + displayName,
            MediaType = "application/octet-stream",
            Length = 1,
            AddedAt = DateTimeOffset.UnixEpoch,
        };
}
