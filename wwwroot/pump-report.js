const format = (value, unit = '') => Number.isFinite(value)
  ? value.toLocaleString('en-US', { maximumFractionDigits: 2, useGrouping: false }) + (unit ? ' ' + unit : '')
  : null;

function column(groups) {
  const table = document.createElement('table');
  const columns = document.createElement('colgroup');
  columns.append(document.createElement('col'), document.createElement('col'));
  table.append(columns);
  for (const [heading, fields] of groups) {
    const body = table.createTBody();
    if (heading) {
      const title = document.createElement('th');
      title.colSpan = 2;
      title.scope = 'rowgroup';
      title.textContent = heading;
      body.insertRow().append(title);
    }
    for (const [label, value] of fields) {
      const row = body.insertRow();
      const name = document.createElement('th');
      name.scope = 'row';
      name.textContent = label;
      row.append(name);
      row.insertCell().textContent = value == null || value === '' ? '-' : value;
    }
  }
  return table;
}

export function fillReport(container, pump, duty, report, details) {
  const ns = report.ns == null ? '-' : Math.round(report.ns);
  const nss = report.nss == null ? '-' : Math.round(report.nss);
  const hasMotor = pump.motorKw != null;
  const header = document.createElement('div');
  header.className = 'report-header';
  header.append(column([[null, [
    ['Item number', details.get('tag')], ['Service', details.get('service')],
    ['Quantity', details.get('quantity')], ['Quote number']
  ]]]), column([[null, [
    ['Pump type / size', pump.modelSeries + ' / ' + pump.size], ['Stages', pump.stages],
    ['Curve number', pump.curve], ['Date last saved']
  ]]]));

  const body = document.createElement('div');
  body.className = 'report-columns';
  body.append(column([
    ['Operating Conditions', [
      ['Flow, rated', format(duty.flow, 'm³/h')],
      ['Differential head, requested', format(duty.head, 'm')],
      ['NPSH available', format(duty.npsha, 'm')],
      ['Suction pressure, rated'], ['Suction pressure, maximum'],
      ['Site supply frequency', format(duty.hz, 'Hz')]
    ]],
    ['Performance', [
      ['Speed criteria', duty.rpm != null ? 'Fixed speed' : duty.poles != null ? duty.poles + ' poles' : null],
      ['Speed, rated', format(pump.rpm, 'rpm')], ['Hydraulic group', pump.group],
      ['Impeller diameter, rated', format(pump.diameter, 'mm')],
      ['Impeller diameter, maximum', format(pump.maxDiameter, 'mm')],
      ['Impeller diameter, minimum', format(pump.minDiameter, 'mm')],
      ['Efficiency', format(pump.efficiency, '%')],
      [pump.sourceRpm || report.viscosityCst > 1 ? 'NPSH required (est.)' : 'NPSH required', format(pump.npshr, 'm')],
      ['NPSH margin required'], ['Impeller suction', pump.suction],
      ['Ns / Nss (rated BEP est.)', ns === '-' && nss === '-' ? null : ns + ' / ' + nss + ' US'],
      ['MCSF (curve estimate)', format(report.mcsfFlow, 'm³/h')],
      ['Shutoff head (rated)', format(report.shutoffHead, 'm')],
      ['Head rise to shutoff', report.shutoffHead > 0 && pump.head > 0
        ? format((report.shutoffHead / pump.head - 1) * 100, '%') : null],
      ['Flow, best efficiency point', format(report.bepFlow, 'm³/h')],
      ['Flow ratio, rated / BEP', report.bepFlow > 0 ? format(duty.flow / report.bepFlow * 100, '%') : null],
      ['Diameter ratio, rated / max', format(pump.diameter / pump.maxDiameter * 100, '%')],
      ['Head ratio, rated / max', format(report.headRatio, '%')],
      [report.mixedStages && report.viscosityFactors ? 'Cq / Ch / Ce (main) / Cn (inlet)' : 'Cq / Ch / Ce / Cn',
        report.viscosityFactors?.map(value => value.toFixed(3)).join(' / ')],
      ['Selection status', pump.sourceRpm ? 'Verify trim and NPSHr' :
        report.viscosityCst > 1 ? 'HI estimate; verify NPSH' : null], ['Orientation', pump.orientation]
    ]]
  ]), column([
    ['Liquid', [
      ['Liquid type', details.get('liquid')], ['Additional liquid description'], ['Solids diameter, maximum'],
      ['Solids concentration, volume'], ['Temperature, rated', format(duty.temperature, '°C')],
      ['Temperature, maximum'], ['Fluid density, rated', format(duty.density, 'kg/m³')],
      ['Fluid density, maximum'], ['Viscosity, rated', duty.viscosityCp == null ? null :
        format(duty.viscosityCp, 'cP') + ' / ' + format(report.viscosityCst, 'cSt')], ['Vapor pressure, rated']
    ]],
    ['Material', [['Material selected', details.get('material')]]],
    ['Pressure Data', [
      ['Outlet pressure'], ['Maximum working pressure'], ['Max. allowable working pressure'],
      ['Max. allowable suction pressure'], ['Hydrostatic test pressure']
    ]],
    ['Driver & Power Data', [
      ['Driver sizing specification', hasMotor ? 'Rated shaft power' : null],
      ['Margin over specification', hasMotor ? format(report.motorMargin, '%') : null], ['Service factor'],
      ['Power, hydraulic', format(report.hydraulicPower, 'kW')], ['Shaft power, rated', format(pump.power, 'kW')],
      ['Power, max, rated diameter', format(report.peakPower, 'kW')],
      ['Minimum recommended motor', format(pump.motorKw, 'kW')], ['Safety power']
    ]]
  ]));
  container.replaceChildren(header, body);
}
