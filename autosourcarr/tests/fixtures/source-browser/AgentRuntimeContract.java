import android.content.ContextWrapper;
import android.content.SharedPreferences;
import eu.kanade.tachiyomi.network.NetworkHelper;
import org.cef.CefSettings;
import java.lang.reflect.Proxy;
import java.util.Map;

public class AgentRuntimeContract {
  static class MemoryContext extends ContextWrapper {
    MemoryContext() { super(null); }
    public SharedPreferences getSharedPreferences(String name, int mode) {
      if (!name.equals("cookie_store")) throw new AssertionError(name);
      final SharedPreferences.Editor editor=(SharedPreferences.Editor) Proxy.newProxyInstance(
        AgentRuntimeContract.class.getClassLoader(),new Class[]{SharedPreferences.Editor.class},
        (proxy,method,args) -> {
          if(method.getName().equals("apply")) return null;
          if(method.getName().equals("commit")) return true;
          return proxy;
        });
      return (SharedPreferences) Proxy.newProxyInstance(
        AgentRuntimeContract.class.getClassLoader(),new Class[]{SharedPreferences.class},
        (proxy,method,args) -> {
          if(method.getName().equals("getAll")) return Map.of();
          if(method.getName().equals("edit")) return editor;
          throw new AssertionError(method.getName());
        });
    }
  }
  public static void main(String[] args) throws Exception {
    String expected=args[0];
    boolean custom=Boolean.parseBoolean(args[1]);
    NetworkHelper network=new NetworkHelper(new MemoryContext());
    if(!network.defaultUserAgentProvider().equals(expected)) throw new AssertionError("Network UA: "+network.defaultUserAgentProvider());
    if(!network.getUserAgentFlow().getValue().equals(expected)) throw new AssertionError("Flow UA");
    CefSettings cef=new CefSettings();
    if(custom && !expected.equals(cef.user_agent)) throw new AssertionError("CEF UA: "+cef.user_agent);
    if(!custom && cef.user_agent!=null) throw new AssertionError("Reset CEF UA");
    if(custom && !expected.equals(cef.clone().user_agent)) throw new AssertionError("CEF clone UA");
    if(custom && !expected.equals(System.getProperty("http.agent"))) throw new AssertionError("Android default UA");
    System.out.println("PASS actual pinned NetworkHelper constructor/provider/flow; actual CefSettings constructor/clone; Android default UA; custom="+custom);
  }
}
