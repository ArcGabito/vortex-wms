// Ayudas mínimas para la UI de Vortex WMS (posicionar menús flotantes y enfocar campos).
window.vortex = {
    rect: (el) => {
        if (!el) return [0, 0, 0, 0, window.innerWidth, window.innerHeight];
        const r = el.getBoundingClientRect();
        return [r.top, r.left, r.bottom, r.right, window.innerWidth, window.innerHeight];
    },
    // En pantallas táctiles no se enfoca solo: abriría el teclado en pantalla y taparía media pantalla.
    getValue: (el) => el ? el.value : null,
    focusNow: (el) => { if (el) el.focus(); },
    setValue: (el, value) => { if (el) el.value = value; },
    focus: (el) => { if (el && !window.matchMedia("(hover: none) and (pointer: coarse)").matches) el.focus(); },
};
