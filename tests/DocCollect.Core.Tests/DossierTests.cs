using DocCollect.Domain;
using DocCollect.Shared;

namespace DocCollect.Core.Tests;

public class DossierTests
{
    [Fact]
    public void FromTemplate_copies_items_in_order()
    {
        var d = Dossier.FromTemplate(TestData.TenantId, "Vente", TestData.Template());
        Assert.Equal(3, d.Items.Count);
        Assert.Equal("Pièce d'identité", d.Items[0].Label);
        Assert.Equal(DossierStatus.Draft, d.Status);
    }

    [Fact]
    public void FromTemplate_refuses_private_template_of_another_tenant()
    {
        var foreign = TestData.Template(TemplateVisibility.Private, Guid.NewGuid());
        var ex = Assert.Throws<DomainException>(() => Dossier.FromTemplate(TestData.TenantId, "Vente", foreign));
        Assert.Equal(ErrorCodes.Validation, ex.Code);
    }

    [Fact]
    public void FromTemplate_accepts_public_template()
    {
        var d = Dossier.FromTemplate(TestData.TenantId, "Vente", TestData.Template(TemplateVisibility.Public));
        Assert.Equal(3, d.Items.Count);
    }

    [Fact]
    public void FromTemplate_requires_at_least_one_item()
    {
        Assert.Throws<DomainException>(() => Dossier.FromTemplate(TestData.TenantId, "Vide", null));
    }

    [Fact]
    public void Missing_items_lists_only_required_ones()
    {
        var d = TestData.OpenDossier();
        var missing = d.MissingRequiredItems().Select(i => i.Label).ToList();
        Assert.Equal(["Pièce d'identité", "Justificatif de domicile"], missing);
        Assert.False(d.IsComplete);
    }

    [Fact]
    public void Dossier_becomes_complete_when_all_required_items_are_provided()
    {
        var d = TestData.OpenDossier();
        d.AddDocument(d.Items[0].Id, TestData.Doc("image/jpeg"), TestData.Now);
        Assert.Equal(DossierStatus.Open, d.Status);
        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.True(d.IsComplete);
        Assert.Equal(DossierStatus.Complete, d.Status);
        Assert.Equal(TestData.Now, d.CompletedAt);
    }

    [Fact]
    public void With_requester_validation_submitted_items_do_not_count_until_accepted()
    {
        var d = TestData.OpenDossier(validation: true);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.False(d.IsComplete);
        Assert.Equal(ItemStatus.Submitted, d.Items[0].Status);

        d.AcceptItem(d.Items[0].Id, TestData.Now);
        d.AcceptItem(d.Items[1].Id, TestData.Now);
        Assert.True(d.IsComplete);
    }

    [Fact]
    public void Reject_removes_undelivered_documents_and_reopens()
    {
        var d = TestData.OpenDossier();
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.Equal(DossierStatus.Complete, d.Status);

        d.RejectItem(d.Items[1].Id, "Document illisible", TestData.Now);
        Assert.Equal(ItemStatus.Rejected, d.Items[1].Status);
        Assert.Empty(d.Items[1].Documents);
        Assert.Equal(DossierStatus.Open, d.Status);
        Assert.Equal("Document illisible", d.Items[1].RejectionReason);
    }

    [Fact]
    public void Reject_requires_a_reason()
    {
        var d = TestData.OpenDossier();
        Assert.Throws<DomainException>(() => d.RejectItem(d.Items[0].Id, " ", TestData.Now));
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/png", true)]
    [InlineData("image/heic", true)]
    [InlineData("application/zip", false)]
    [InlineData("text/plain", false)]
    public void Content_type_rules_support_wildcards(string contentType, bool allowed)
    {
        Assert.Equal(allowed, ContentTypeMatcher.IsAllowed(contentType, ["image/*", "application/pdf"]));
    }

    [Fact]
    public void Empty_accepted_list_allows_everything()
    {
        Assert.True(ContentTypeMatcher.IsAllowed("application/whatever", []));
    }

    [Fact]
    public void Upload_rejects_too_large_files()
    {
        var d = TestData.OpenDossier();
        var ex = Assert.Throws<DomainException>(() => d.EnsureCanUpload(d.Items[0].Id, "application/pdf", 6L * 1024 * 1024));
        Assert.Equal(ErrorCodes.FileTooLarge, ex.Code);
    }

    [Fact]
    public void Upload_rejects_wrong_type()
    {
        var d = TestData.OpenDossier();
        var ex = Assert.Throws<DomainException>(() => d.EnsureCanUpload(d.Items[1].Id, "image/png", 100));
        Assert.Equal(ErrorCodes.ContentTypeNotAllowed, ex.Code);
    }

    [Fact]
    public void Upload_respects_max_files()
    {
        var d = TestData.OpenDossier();
        var diag = d.Items[2];
        for (var i = 0; i < 3; i++) d.AddDocument(diag.Id, TestData.Doc(), TestData.Now);
        var ex = Assert.Throws<DomainException>(() => d.AddDocument(diag.Id, TestData.Doc(), TestData.Now));
        Assert.Equal(ErrorCodes.TooManyFiles, ex.Code);
    }

    [Fact]
    public void Draft_and_cancelled_dossiers_refuse_uploads()
    {
        var draft = Dossier.FromTemplate(TestData.TenantId, "Brouillon", TestData.Template());
        Assert.Throws<DomainException>(() => draft.EnsureCanUpload(draft.Items[0].Id, "application/pdf", 10));

        var cancelled = TestData.OpenDossier();
        cancelled.Cancel();
        Assert.Throws<DomainException>(() => cancelled.EnsureCanUpload(cancelled.Items[0].Id, "application/pdf", 10));
    }

    [Fact]
    public void AllAtOnce_delivers_nothing_until_complete_then_everything()
    {
        var d = TestData.OpenDossier(DeliveryMode.AllAtOnce);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        d.AddDocument(d.Items[2].Id, TestData.Doc(), TestData.Now); // facultatif
        Assert.Empty(d.DocumentsReadyForDelivery());

        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.Equal(3, d.DocumentsReadyForDelivery().Count);
    }

    [Fact]
    public void Incremental_delivers_each_document_once()
    {
        var d = TestData.OpenDossier(DeliveryMode.Incremental);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        var first = d.DocumentsReadyForDelivery();
        Assert.Single(first);

        d.MarkDelivered(first.Select(x => x.Id), TestData.Now);
        Assert.Empty(d.DocumentsReadyForDelivery());

        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        Assert.Single(d.DocumentsReadyForDelivery());
    }

    [Fact]
    public void Incremental_with_validation_waits_for_acceptance()
    {
        var d = TestData.OpenDossier(DeliveryMode.Incremental, validation: true);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        Assert.Empty(d.DocumentsReadyForDelivery());
        d.AcceptItem(d.Items[0].Id, TestData.Now);
        Assert.Single(d.DocumentsReadyForDelivery());
    }

    [Fact]
    public void Delivered_dossier_goes_back_to_complete_when_a_new_document_arrives()
    {
        var d = TestData.OpenDossier(DeliveryMode.Incremental);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        d.AddDocument(d.Items[1].Id, TestData.Doc(), TestData.Now);
        d.MarkDelivered(d.DocumentsReadyForDelivery().Select(x => x.Id), TestData.Now);
        Assert.Equal(DossierStatus.Delivered, d.Status);

        d.AddDocument(d.Items[2].Id, TestData.Doc(), TestData.Now);
        Assert.Equal(DossierStatus.Complete, d.Status);
        Assert.Single(d.DocumentsReadyForDelivery());
    }

    [Fact]
    public void Delivered_document_cannot_be_removed()
    {
        var d = TestData.OpenDossier(DeliveryMode.Incremental);
        var doc = TestData.Doc();
        d.AddDocument(d.Items[0].Id, doc, TestData.Now);
        d.MarkDelivered([doc.Id], TestData.Now);
        var ex = Assert.Throws<DomainException>(() => d.RemoveDocument(doc.Id, TestData.Now));
        Assert.Equal(ErrorCodes.AlreadyDelivered, ex.Code);
    }

    [Fact]
    public void Removing_last_document_marks_item_missing()
    {
        var d = TestData.OpenDossier();
        var doc = TestData.Doc();
        d.AddDocument(d.Items[0].Id, doc, TestData.Now);
        d.RemoveDocument(doc.Id, TestData.Now);
        Assert.Equal(ItemStatus.Missing, d.Items[0].Status);
    }

    [Fact]
    public void Manual_delivery_sends_satisfied_items_even_if_incomplete()
    {
        var d = TestData.OpenDossier(DeliveryMode.AllAtOnce);
        d.AddDocument(d.Items[0].Id, TestData.Doc(), TestData.Now);
        Assert.Empty(d.DocumentsReadyForDelivery());
        Assert.Single(d.DocumentsForManualDelivery());
    }
}
