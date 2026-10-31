/* Presentation calculations. Unknown source/library observations stay unknown. */
(function (root, factory) {
  const core = factory();
  if (typeof module === 'object' && module.exports) module.exports = core;
  else root.AutoSourcarrReimaginedCore = core;
})(typeof globalThis === 'object' ? globalThis : this, function () {
  'use strict';
  const routes = {
    '/series':{tab:'subbed',scope:'all'}, '/series/monitored':{tab:'subbed',scope:'monitored'}, '/catalog':{tab:'subbed',scope:'catalog'}, '/add':{tab:'browse'},
    '/matching':{tab:'matrix'}, '/duplicates':{tab:'duplicates'},
    '/mixed':{tab:'mixed-entries'}, '/decisions':{tab:'manual-actions'},
    '/activity':{tab:'tasks',section:'general'}, '/activity/repairs':{tab:'tasks',section:'repairs'},
    '/imports':{tab:'imports'}, '/wanted':{tab:'subbed',scope:'missing_downloads'},
    '/wanted/sources':{tab:'subbed',scope:'source_gaps'}, '/wanted/verification':{tab:'subbed',scope:'verification'},
    '/unmatched':{tab:'subbed',scope:'unmatched'}, '/attention':{tab:'attention'},
    '/sources':{tab:'sources'}, '/sources/health':{tab:'sources',section:'health'},
    '/sources/manage':{tab:'sources',section:'manage'}, '/sources/repositories':{tab:'sources',section:'repositories'},
    '/sources/ai':{tab:'sources',section:'maintenance'},
    '/settings':{tab:'settings'}, '/settings/configuration':{tab:'settings',section:'configuration'},
    '/settings/appearance':{tab:'settings',section:'customization'},
    '/system':{tab:'about'}, '/statistics':{tab:'statistics'},
  };
  function parseRoute(hash) {
    const path = String(hash || '').replace(/^#/, '');
    return Object.hasOwn(routes, path) ? {...routes[path]} : null;
  }
  function count(value) {
    return value !== null && value !== undefined && value !== '' && Number.isFinite(Number(value)) && Number(value)>=0 ? Number(value) : null;
  }
  function chapterProgress(stats) {
    const downloaded=count(stats?.downloaded_chapter_count), total=count(stats?.chapter_count);
    return {downloaded,total,percent:total>0 && downloaded!==null ? Math.min(100,Math.round(downloaded/total*100)) : null,complete:total>0 && downloaded!==null && downloaded>=total};
  }
  function summarizeHealth(health) {
    const catalog=health?.stats?.catalog||{}, library=health?.library||{};
    const entries=count(catalog.entries),matched=count(catalog.matched_entries);
    return {entries,matched,matchedPercent:entries>0 && matched!==null ? Math.min(100,Math.round(matched/entries*100)) : null,
      subscribed:library.ok===true?count(library.subbed_entries):null,
      downloaded:library.ok===true?count(library.downloaded_chapters):null,
      sources:count(health?.stats?.sources?.enabled_families),
      chapters:count(health?.stats?.chapters?.virtual_chapters),
      candidates:count(health?.stats?.chapters?.chapter_candidates),
      history:count(health?.stats?.chapters?.served_history),
      unmatched:count(catalog.unmatched_entries),duplicates:count(catalog.possible_duplicates)};
  }
  function previewController() {
    let generation=0,current=null;
    return {
      invalidate(){generation++;current=null;},
      current(){return current;},
      async preview(operation){
        const own=++generation;current=null;
        const value=await operation();
        if(own!==generation)return null;
        current=value;return value;
      }
    };
  }
  return {parseRoute, chapterProgress, summarizeHealth,previewController};
});
