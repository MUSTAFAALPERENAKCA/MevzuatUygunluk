(function () {
    const tbl = document.getElementById('resultsTable');
    if (!tbl) return;

    const filterMust = document.getElementById('filterMust');
    const filterPresent = document.getElementById('filterPresent');

    function applyFilters() {
        const must = filterMust.value;
        const present = filterPresent.value;
        const rows = tbl.querySelectorAll('tbody tr');
        rows.forEach(r => {
            const rm = r.getAttribute('data-must');
            const rp = r.getAttribute('data-present');
            let ok = true;
            if (must === 'true' && rm !== 'true') ok = false;
            if (must === 'false' && rm !== 'false') ok = false;
            if (present === 'true' && rp !== 'true') ok = false;
            if (present === 'false' && rp !== 'false') ok = false;
            r.style.display = ok ? '' : 'none';
        });
    }
    filterMust && filterMust.addEventListener('change', applyFilters);
    filterPresent && filterPresent.addEventListener('change', applyFilters);

    // Dayanak modal
    const detailModal = new bootstrap.Modal(document.getElementById('modalDetail'));
    const detailBody = document.getElementById('detailBody');
    tbl.addEventListener('click', function (e) {
        const btn = e.target.closest('.btn-detail');
        if (!btn) return;
        const req = btn.getAttribute('data-req') || '';
        const law = JSON.parse(btn.getAttribute('data-law') || '[]');
        const files = JSON.parse(btn.getAttribute('data-files') || '[]');

        let html = `<h6 class="mb-2">${req}</h6>`;
        if (law.length) {
            html += `<div class="mb-3"><div class="fw-semibold">Mevzuat Dayanakları</div><ul class="list-group">` +
                law.map(x => `<li class="list-group-item"><div><b>Kaynak:</b> ${x.docName} ${x.page ? "(s. " + x.page + ")" : ""}</div><div>${x.quote || ""}</div></li>`).join('') +
                `</ul></div>`;
        }
        if (files.length) {
            html += `<div class="mb-2"><div class="fw-semibold">Dosya Bazında Kanıt</div><ul class="list-group">` +
                files.map(x => `<li class="list-group-item"><div><b>${x.file}</b> — ${x.present ? "Var" : "Yok"}</div><div>${x.evidence || "-"}</div><div>${(x.pages || []).join(",")}</div></li>`).join('') +
                `</ul></div>`;
        }
        if (!law.length && !files.length) {
            html += `<div class="text-muted">Ayrıntı bulunamadı.</div>`;
        }
        detailBody.innerHTML = html;
        detailModal.show();
    });

    // HITL feedback
    const fbModal = new bootstrap.Modal(document.getElementById('modalFeedback'));
    const fbReq = document.getElementById('fbReq');
    const fbPresent = document.getElementById('fbPresent');
    const fbEvidence = document.getElementById('fbEvidence');

    tbl.addEventListener('click', function (e) {
        const b = e.target.closest('.btn-feedback');
        if (!b) return;
        fbReq.value = b.getAttribute('data-req') || '';
        fbPresent.value = '';
        fbEvidence.value = '';
        fbModal.show();
    });

    document.getElementById('btnSendFb').addEventListener('click', async function () {
        const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
        const payload = {
            requirement: fbReq.value,
            present: fbPresent.value === '' ? null : (fbPresent.value === 'true'),
            evidence: fbEvidence.value,
            scenario: document.querySelector('input[name="Scenario"]:checked')?.value || '',
            invoiceType: document.getElementById('InvoiceType')?.value || ''
        };
        const resp = await fetch('/Docs/SaveFeedback', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify(payload)
        });
        if (resp.ok) {
            fbModal.hide();
            alert('Düzeltme kaydedildi. Bir sonraki denetimde dikkate alınacak.');
        } else {
            alert('Kaydedilemedi.');
        }
    });
})();
