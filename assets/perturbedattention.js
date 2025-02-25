postParamBuildSteps.push(() => {
    let groups = [
        "perturbedattentionguidanceadvanced",
        "slidingwindowguidance",
    ];
    for (const group of groups) {
        const groupEl = document.getElementById(`input_group_content_${group}`);
        if (groupEl) {
            if (!currentBackendFeatureSet.includes("perturbedattention")) {
                const buttonId = `${group}_install_button`;
                groupEl.append(
                    createDiv(
                        buttonId,
                        "keep_group_visible",
                        `<button class="basic-button" onclick="installFeatureById('perturbedattention', ${buttonId})">Install sd-perturbed-attention</button>`
                    )
                );
            }
        }
    }
});
