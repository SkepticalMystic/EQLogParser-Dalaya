var GUIDES = [
  { id: "installation",       title: "Installation",              ready: true },
  { id: "parser",             title: "Using the parser",          ready: true },
  { id: "raid-export",        title: "Exporting for raid DPS",    ready: true },
  { id: "raid-dps",           title: "Using raid DPS",            ready: true },
  { id: "creating-triggers",  title: "Creating triggers",         ready: true },
  { id: "trigger-variables",  title: "Trigger variables",         ready: true },
  { id: "regex",              title: "Regex reference",           ready: true },
  { id: "dalaya-vs-upstream", title: "Dalaya vs upstream",        ready: true },
  { id: "spell-parser",       title: "Spell parser",              ready: true },
  { id: "faq",                title: "FAQ",                       ready: true },
];

function renderNav(currentId) {
  var currentIndex = -1;
  for (var i = 0; i < GUIDES.length; i++) {
    if (GUIDES[i].id === currentId) { currentIndex = i; break; }
  }

  var sidebarEl = document.getElementById('guide-sidebar');
  if (sidebarEl) {
    var html = '<p class="guide-sidebar-heading">Guides</p><ul class="guide-sidebar-list">';
    for (var i = 0; i < GUIDES.length; i++) {
      var g = GUIDES[i];
      if (g.id === currentId) {
        html += '<li><span class="guide-sidebar-link current">' + g.title + '</span></li>';
      } else if (g.ready) {
        html += '<li><a class="guide-sidebar-link" href="' + g.id + '.html">' + g.title + '</a></li>';
      } else {
        html += '<li><span class="guide-sidebar-link soon">' + g.title + '<span class="soon-badge">soon</span></span></li>';
      }
    }
    html += '</ul>';
    sidebarEl.innerHTML = html;
  }

  var prevnextEl = document.getElementById('guide-prevnext');
  if (prevnextEl) {
    var prev = currentIndex > 0 ? GUIDES[currentIndex - 1] : null;
    var next = currentIndex < GUIDES.length - 1 ? GUIDES[currentIndex + 1] : null;
    var html = '';
    if (prev && prev.ready) {
      html += '<a class="prevnext-btn" href="' + prev.id + '.html">← ' + prev.title + '</a>';
    } else {
      html += '<span></span>';
    }
    if (next && next.ready) {
      html += '<a class="prevnext-btn" href="' + next.id + '.html">' + next.title + ' →</a>';
    } else {
      html += '<span></span>';
    }
    prevnextEl.innerHTML = html;
  }
}
